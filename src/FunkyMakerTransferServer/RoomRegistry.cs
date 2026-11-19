using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FunkyMakerTransferServer;

/// <summary>A message to send to one connection after a registry call.</summary>
public sealed record Outbound(string ConnectionId, string Method, object Payload);

/// <summary>
/// Side effects collected while the registry lock is held and executed after it is released
/// (by <see cref="EffectRunner"/>): hub messages, cancelling running HTTP streams, deleting files.
/// </summary>
public sealed class Effects
{
    public List<Outbound> Messages { get; } = new();
    public List<CancellationTokenSource> ToCancel { get; } = new();
    public List<string> FilesToDelete { get; } = new();

    internal void Send(string connectionId, string method, object payload) =>
        Messages.Add(new Outbound(connectionId, method, payload));
}

public sealed record UploadTicket(string TransferId, string Hash, long Size, CancellationToken Token);
public sealed record DownloadTicket(string TransferId, string FilePath, long Size, string FileName, CancellationToken Token);
public sealed record RegistryStats(int Rooms, int Members, int Transfers, int CachedFiles);

/// <summary>
/// All room, queue and transfer state. Every public method takes the single lock, so the state is
/// always consistent; network I/O happens outside it through the returned <see cref="Effects"/>.
///
/// Flow (see README): players join a room with their level list → each player gets a personalised
/// view of the room's levels → a player queues for a level hash → two queued players are paired
/// when at least one of them has the file → if only one has it, a transfer runs (upload, then
/// download, then the downloader verifies the hash) → both get MatchReady with a fresh private code
/// for the game server.
/// </summary>
public sealed partial class RoomRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Member> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly TransferOptions _options;
    private readonly TimeProvider _time;
    private long _queueSeq;

    public RoomRegistry(IOptions<TransferOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
    }

    public long MaxUploadBytes => _options.MaxUploadBytes;

    [GeneratedRegex("^[A-Z0-9]{6}$")]
    private static partial Regex RoomCodeRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    // ── Rooms ────────────────────────────────────────────────────────────────

    public JoinRoomResult JoinRoom(string connectionId, JoinRoomRequest request, Effects fx)
    {
        if (request.ProtocolVersion != Protocol.Version)
            return Fail($"Transfer protocol mismatch (server {Protocol.Version}, game {request.ProtocolVersion}). Update the game.");

        string code = (request.RoomCode ?? "").Trim().ToUpperInvariant();
        if (!RoomCodeRegex().IsMatch(code))
            return Fail("Invalid room code.");

        lock (_lock)
        {
            if (_members.TryGetValue(connectionId, out var existing))
                RemoveMemberLocked(existing, fx);

            if (!_rooms.TryGetValue(code, out var room))
            {
                room = new Room(code);
                _rooms[code] = room;
            }

            // Same player on a new connection (reconnect): drop the stale entry.
            string playerId = Clean(request.PlayerId, 64);
            if (playerId.Length > 0)
            {
                var stale = room.Members.FirstOrDefault(m => m.PlayerId == playerId);
                if (stale != null)
                    RemoveMemberLocked(stale, fx);
            }

            if (room.Members.Count >= _options.MaxRoomMembers)
            {
                if (room.Members.Count == 0) _rooms.Remove(code);
                return Fail("This room is full.");
            }

            var member = new Member(connectionId, playerId, CleanName(request.Username, "Player"), room)
            {
                Levels = SanitizeLevels(request.Levels),
            };
            room.Members.Add(member);
            _members[connectionId] = member;
            BroadcastRoomLocked(room, fx);
        }

        return new JoinRoomResult { Ok = true, MaxUploadBytes = _options.MaxUploadBytes };

        static JoinRoomResult Fail(string error) => new() { Ok = false, Error = error };
    }

    public void UpdateLevels(string connectionId, List<LevelEntry>? levels, Effects fx)
    {
        lock (_lock)
        {
            if (!_members.TryGetValue(connectionId, out var member)) return;
            member.Levels = SanitizeLevels(levels);
            if (member.QueueHash != null)
                TryPairLocked(member.Room, member.QueueHash, fx);
            BroadcastRoomLocked(member.Room, fx);
        }
    }

    public void LeaveRoom(string connectionId, Effects fx)
    {
        lock (_lock)
        {
            if (_members.TryGetValue(connectionId, out var member))
                RemoveMemberLocked(member, fx);
        }
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    public QueueResult JoinQueue(string connectionId, string hash, Effects fx)
    {
        hash = (hash ?? "").Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_members.TryGetValue(connectionId, out var member))
                return new QueueResult { Error = "Join a room first." };
            if (member.Transfer != null)
                return new QueueResult { Error = "A transfer is already in progress." };
            if (!HashRegex().IsMatch(hash))
                return new QueueResult { Error = "Invalid level hash." };
            if (!member.Room.Members.Any(m => m.Levels.ContainsKey(hash)))
                return new QueueResult { Error = "Nobody in this room has that level any more." };

            member.QueueHash = hash;
            member.QueueSeq = ++_queueSeq;
            TryPairLocked(member.Room, hash, fx);
            BroadcastRoomLocked(member.Room, fx);
            return new QueueResult { Ok = true };
        }
    }

    public void LeaveQueue(string connectionId, Effects fx)
    {
        lock (_lock)
        {
            if (!_members.TryGetValue(connectionId, out var member)) return;
            member.QueueHash = null;
            if (member.Transfer != null)
                CancelTransferLocked(member.Transfer, fx, "Your opponent cancelled.", requeue: m => m != member, notifyMember: m => m != member);
            BroadcastRoomLocked(member.Room, fx);
        }
    }

    // ── Transfers (HTTP side) ────────────────────────────────────────────────

    public UploadTicket? BeginUpload(string transferId, string token, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || !TokenEquals(t.UploadToken, token))
            {
                error = "Unknown transfer.";
                return null;
            }
            if (t.Phase != TransferPhase.Upload || t.UploadStarted)
            {
                error = "This transfer is not waiting for an upload.";
                return null;
            }
            t.UploadStarted = true;
            t.LastActivity = Now;
            error = "";
            return new UploadTicket(t.Id, t.Hash, t.Size, t.Cts.Token);
        }
    }

    /// <summary>Records streamed bytes for the current phase; sends throttled progress to both players.</summary>
    public void ReportBytes(string transferId, string phase, long bytes, Effects fx)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || PhaseName(t.Phase) != phase) return;
            t.Bytes = bytes;
            var now = Now;
            t.LastActivity = now;
            if (bytes >= t.Size || (now - t.LastProgressSent).TotalMilliseconds >= _options.ProgressIntervalMs)
            {
                t.LastProgressSent = now;
                SendProgressLocked(t, fx);
            }
        }
    }

    /// <summary>The upload finished; checks it and moves the transfer to the download phase.</summary>
    public bool CompleteUpload(string transferId, string path, string actualHash, long actualSize, Effects fx, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.Phase != TransferPhase.Upload)
            {
                fx.FilesToDelete.Add(path);
                error = "The transfer was cancelled.";
                return false;
            }
            if (!string.Equals(actualHash, t.Hash, StringComparison.OrdinalIgnoreCase) || actualSize != t.Size)
            {
                fx.FilesToDelete.Add(path);
                CancelTransferLocked(t, fx, "The uploaded file did not match the level (it may have changed on disk).");
                error = "Uploaded file does not match the announced hash/size.";
                return false;
            }

            t.FilePath = path;
            if (t.Room.Cache.ContainsKey(t.Hash))
            {
                t.OwnsFile = true; // another copy is already cached; delete this one when done
            }
            else
            {
                t.Room.Cache[t.Hash] = new CachedFile(path, t.Size) { ExpiresAt = Now.AddMinutes(_options.CacheMinutes) };
            }

            StartDownloadPhaseLocked(t, fx);
            error = "";
            return true;
        }
    }

    /// <summary>The upload stream failed or was aborted.</summary>
    public void FailUpload(string transferId, string reason, string path, Effects fx)
    {
        fx.FilesToDelete.Add(path);
        lock (_lock)
        {
            if (_transfers.TryGetValue(transferId, out var t) && t.Phase == TransferPhase.Upload)
                CancelTransferLocked(t, fx, reason);
        }
    }

    public DownloadTicket? BeginDownload(string transferId, string token, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || !TokenEquals(t.DownloadToken, token))
            {
                error = "Unknown transfer.";
                return null;
            }
            if (t.Phase != TransferPhase.Download || t.FilePath == null)
            {
                error = "This transfer is not ready for download.";
                return null;
            }
            t.LastActivity = Now;
            t.Bytes = 0;
            if (t.Room.Cache.TryGetValue(t.Hash, out var cached) && cached.Path == t.FilePath)
                cached.ExpiresAt = Now.AddMinutes(_options.CacheMinutes);
            error = "";
            return new DownloadTicket(t.Id, t.FilePath, t.Size, t.FileName, t.Cts.Token);
        }
    }

    /// <summary>All bytes were written to the downloader; waiting for their hash check.</summary>
    public void DownloadSent(string transferId, Effects fx)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.Phase != TransferPhase.Download) return;
            t.Phase = TransferPhase.Verify;
            t.Bytes = t.Size;
            t.LastActivity = Now;
            SendProgressLocked(t, fx);
        }
    }

    // ── Transfers (hub side) ─────────────────────────────────────────────────

    /// <summary>The downloader reports whether the saved file matched the expected hash.</summary>
    public void CompleteTransfer(string connectionId, string transferId, bool ok, string? error, Effects fx)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.Downloader.ConnectionId != connectionId) return;
            if (t.Phase == TransferPhase.Upload) return;

            if (!ok)
            {
                string why = string.IsNullOrWhiteSpace(error) ? "verification failed" : Clean(error, 160);
                CancelTransferLocked(t, fx, $"Download failed: {why}");
                return;
            }

            var entry = new LevelEntry { Hash = t.Hash, Name = t.Name, FileName = t.FileName, Size = t.Size };
            EndTransferLocked(t, fx);
            t.Downloader.Levels[t.Hash] = entry; // they have it now (their next UpdateLevels confirms)
            SendMatchReadyLocked(t.Uploader, t.Downloader, entry, fx);
            BroadcastRoomLocked(t.Room, fx);
        }
    }

    // ── Maintenance ──────────────────────────────────────────────────────────

    /// <summary>Cancels stalled transfers and drops expired cached files.</summary>
    public void Sweep(Effects fx)
    {
        lock (_lock)
        {
            var now = Now;
            var stall = TimeSpan.FromSeconds(_options.StallTimeoutSeconds);
            foreach (var t in _transfers.Values.Where(t => now - t.LastActivity > stall).ToList())
                CancelTransferLocked(t, fx, "The transfer timed out.");

            foreach (var room in _rooms.Values)
            {
                foreach (var (hash, file) in room.Cache.ToList())
                {
                    if (file.ExpiresAt > now) continue;
                    if (_transfers.Values.Any(t => t.FilePath == file.Path)) continue;
                    room.Cache.Remove(hash);
                    fx.FilesToDelete.Add(file.Path);
                }
            }
        }
    }

    public RegistryStats GetStats()
    {
        lock (_lock)
            return new RegistryStats(_rooms.Count, _members.Count, _transfers.Count, _rooms.Values.Sum(r => r.Cache.Count));
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private DateTimeOffset Now => _time.GetUtcNow();

    private void RemoveMemberLocked(Member member, Effects fx)
    {
        var room = member.Room;
        member.QueueHash = null;
        if (member.Transfer != null)
            CancelTransferLocked(member.Transfer, fx, "Your opponent left the room.", requeue: m => m != member, notifyMember: m => m != member);

        room.Members.Remove(member);
        _members.Remove(member.ConnectionId);

        if (room.Members.Count == 0)
        {
            _rooms.Remove(room.Code);
            foreach (var file in room.Cache.Values)
                fx.FilesToDelete.Add(file.Path);
            room.Cache.Clear();
        }
        else
        {
            BroadcastRoomLocked(room, fx);
        }
    }

    /// <summary>
    /// Pairs queued players for <paramref name="hash"/>, oldest first, as long as each pair has at
    /// least one player with the file. Two players without the file are never paired together.
    /// </summary>
    private void TryPairLocked(Room room, string hash, Effects fx)
    {
        while (true)
        {
            var queue = room.Members
                .Where(m => m.QueueHash == hash && m.Transfer == null)
                .OrderBy(m => m.QueueSeq)
                .ToList();

            Member? a = null, b = null;
            for (int i = 0; i < queue.Count && a == null; i++)
            {
                for (int j = i + 1; j < queue.Count; j++)
                {
                    if (queue[i].Levels.ContainsKey(hash) || queue[j].Levels.ContainsKey(hash))
                    {
                        a = queue[i];
                        b = queue[j];
                        break;
                    }
                }
            }
            if (a == null || b == null) return;
            PairLocked(room, hash, a, b, fx);
        }
    }

    private void PairLocked(Room room, string hash, Member a, Member b, Effects fx)
    {
        a.QueueHash = null;
        b.QueueHash = null;
        bool aHas = a.Levels.TryGetValue(hash, out var aEntry);
        bool bHas = b.Levels.TryGetValue(hash, out var bEntry);
        var entry = (aHas ? aEntry : bEntry)!;

        if (aHas && bHas)
        {
            SendMatchReadyLocked(a, b, entry, fx);
            return;
        }

        var uploader = aHas ? a : b;
        var downloader = aHas ? b : a;
        var now = Now;
        var t = new Transfer(NewId(16), room, hash, entry.Name, entry.FileName, entry.Size, uploader, downloader,
            NewId(24), NewId(24))
        {
            LastActivity = now,
            LastProgressSent = now,
        };
        _transfers[t.Id] = t;
        uploader.Transfer = t;
        downloader.Transfer = t;

        bool fromCache = room.Cache.TryGetValue(hash, out var cached) && File.Exists(cached.Path);
        if (fromCache)
        {
            t.FilePath = cached!.Path;
            t.Phase = TransferPhase.Download;
            cached.ExpiresAt = now.AddMinutes(_options.CacheMinutes);
        }

        string phase = PhaseName(t.Phase);
        fx.Send(uploader.ConnectionId, Protocol.TransferStarted, StartedPacket(t, Protocol.RoleUpload, phase, t.UploadToken, downloader));
        fx.Send(downloader.ConnectionId, Protocol.TransferStarted, StartedPacket(t, Protocol.RoleDownload, phase, t.DownloadToken, uploader));

        if (fromCache)
            StartDownloadPhaseLocked(t, fx);
        else
            SendProgressLocked(t, fx);
    }

    private void StartDownloadPhaseLocked(Transfer t, Effects fx)
    {
        t.Phase = TransferPhase.Download;
        t.Bytes = 0;
        t.LastActivity = Now;
        SendProgressLocked(t, fx);
        fx.Send(t.Downloader.ConnectionId, Protocol.ReadyToDownload, new ReadyToDownloadPacket { TransferId = t.Id });
    }

    private void SendMatchReadyLocked(Member a, Member b, LevelEntry entry, Effects fx)
    {
        string code = NewMatchCode();
        fx.Send(a.ConnectionId, Protocol.MatchReady, new MatchReadyPacket
        {
            Hash = entry.Hash, Name = entry.Name, FileName = entry.FileName, MatchCode = code, OpponentName = b.Username,
        });
        fx.Send(b.ConnectionId, Protocol.MatchReady, new MatchReadyPacket
        {
            Hash = entry.Hash, Name = entry.Name, FileName = entry.FileName, MatchCode = code, OpponentName = a.Username,
        });
    }

    /// <summary>
    /// Ends a transfer and tells the players. <paramref name="requeue"/> picks who goes back into the
    /// queue for the same level (by default nobody); <paramref name="notifyMember"/> who is told.
    /// </summary>
    private void CancelTransferLocked(Transfer t, Effects fx, string reason,
        Func<Member, bool>? requeue = null, Func<Member, bool>? notifyMember = null)
    {
        EndTransferLocked(t, fx);
        bool anyRequeued = false;
        foreach (var p in new[] { t.Uploader, t.Downloader })
        {
            if (!_members.ContainsKey(p.ConnectionId)) continue;
            bool back = requeue?.Invoke(p) ?? false;
            if (back)
            {
                p.QueueHash = t.Hash; // keeps the original QueueSeq, so they keep their place
                anyRequeued = true;
            }
            if (notifyMember?.Invoke(p) ?? true)
            {
                fx.Send(p.ConnectionId, Protocol.TransferCancelled, new TransferCancelledPacket
                {
                    TransferId = t.Id, Reason = reason, Requeued = back,
                });
            }
        }
        if (anyRequeued)
            TryPairLocked(t.Room, t.Hash, fx);
        BroadcastRoomLocked(t.Room, fx);
    }

    private void EndTransferLocked(Transfer t, Effects fx)
    {
        _transfers.Remove(t.Id);
        if (t.Uploader.Transfer == t) t.Uploader.Transfer = null;
        if (t.Downloader.Transfer == t) t.Downloader.Transfer = null;
        fx.ToCancel.Add(t.Cts);
        if (t.OwnsFile && t.FilePath != null)
            fx.FilesToDelete.Add(t.FilePath);
    }

    private void SendProgressLocked(Transfer t, Effects fx)
    {
        var packet = new TransferProgressPacket { TransferId = t.Id, Phase = PhaseName(t.Phase), Bytes = t.Bytes, Total = t.Size };
        fx.Send(t.Uploader.ConnectionId, Protocol.TransferProgress, packet);
        fx.Send(t.Downloader.ConnectionId, Protocol.TransferProgress, packet);
    }

    /// <summary>Sends every member their own view of the room plus their queue state.</summary>
    private void BroadcastRoomLocked(Room room, Effects fx)
    {
        // Union of all levels in the room, keyed by hash.
        var all = new Dictionary<string, LevelEntry>(StringComparer.Ordinal);
        foreach (var m in room.Members)
            foreach (var (hash, entry) in m.Levels)
                all.TryAdd(hash, entry);

        var waiting = room.Members
            .Where(m => m.QueueHash != null)
            .GroupBy(m => m.QueueHash!)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var member in room.Members)
        {
            var levels = new List<RoomLevel>(all.Count);
            foreach (var (hash, entry) in all)
            {
                bool youHave = member.Levels.TryGetValue(hash, out var own);
                var src = own ?? entry;
                levels.Add(new RoomLevel
                {
                    Hash = hash,
                    Name = src.Name,
                    FileName = src.FileName,
                    Size = src.Size,
                    YouHave = youHave,
                    OthersHave = room.Members.Count(o => o != member && o.Levels.ContainsKey(hash)),
                    Waiting = waiting.GetValueOrDefault(hash),
                });
            }
            fx.Send(member.ConnectionId, Protocol.RoomState, new RoomStatePacket
            {
                RoomCode = room.Code, Members = room.Members.Count, Levels = levels,
            });

            var q = new QueueStatePacket();
            if (member.QueueHash != null)
            {
                q.Hash = member.QueueHash;
                q.YouHave = member.Levels.ContainsKey(member.QueueHash);
                q.Waiting = waiting.GetValueOrDefault(member.QueueHash);
                q.HoldersInRoom = room.Members.Count(o => o.Levels.ContainsKey(member.QueueHash));
            }
            fx.Send(member.ConnectionId, Protocol.QueueState, q);
        }
    }

    private Dictionary<string, LevelEntry> SanitizeLevels(List<LevelEntry>? levels)
    {
        var result = new Dictionary<string, LevelEntry>(StringComparer.Ordinal);
        if (levels == null) return result;
        foreach (var l in levels)
        {
            if (result.Count >= _options.MaxLevelsPerMember) break;
            if (l == null) continue;
            string hash = (l.Hash ?? "").Trim().ToLowerInvariant();
            if (!HashRegex().IsMatch(hash) || l.Size <= 0 || l.Size > _options.MaxUploadBytes) continue;
            string fileName = CleanFileName(l.FileName);
            if (fileName.Length == 0) continue;
            string name = CleanName(l.Name, Path.GetFileNameWithoutExtension(fileName));
            result.TryAdd(hash, new LevelEntry { Hash = hash, Name = name, FileName = fileName, Size = l.Size });
        }
        return result;
    }

    internal static string CleanFileName(string? fileName)
    {
        string name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/').Last());
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length > 120) name = name[^120..];
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".fnmm" or ".zip" or ".rar" && name.Length > ext.Length ? name : "";
    }

    private static string CleanName(string? value, string fallback)
    {
        string s = Clean(value, 100);
        return s.Length > 0 ? s : Clean(fallback, 100);
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (char c in value)
        {
            if (sb.Length >= max) break;
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static bool TokenEquals(string expected, string? actual) =>
        actual != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string NewId(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private static string NewMatchCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return RandomNumberGenerator.GetString(chars, 6);
    }

    private static TransferStartedPacket StartedPacket(Transfer t, string role, string phase, string token, Member opponent) => new()
    {
        TransferId = t.Id, Hash = t.Hash, Name = t.Name, FileName = t.FileName, Size = t.Size,
        Role = role, Phase = phase, Token = token, OpponentName = opponent.Username,
    };

    internal static string PhaseName(TransferPhase p) => p switch
    {
        TransferPhase.Upload => Protocol.PhaseUpload,
        TransferPhase.Download => Protocol.PhaseDownload,
        _ => Protocol.PhaseVerify,
    };

    internal enum TransferPhase { Upload, Download, Verify }

    private sealed class Room(string code)
    {
        public string Code { get; } = code;
        public List<Member> Members { get; } = new();
        public Dictionary<string, CachedFile> Cache { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Member(string connectionId, string playerId, string username, Room room)
    {
        public string ConnectionId { get; } = connectionId;
        public string PlayerId { get; } = playerId;
        public string Username { get; } = username;
        public Room Room { get; } = room;
        public Dictionary<string, LevelEntry> Levels { get; set; } = new();
        public string? QueueHash { get; set; }
        public long QueueSeq { get; set; }
        public Transfer? Transfer { get; set; }
    }

    private sealed class CachedFile(string path, long size)
    {
        public string Path { get; } = path;
        public long Size { get; } = size;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class Transfer(string id, Room room, string hash, string name, string fileName, long size,
        Member uploader, Member downloader, string uploadToken, string downloadToken)
    {
        public string Id { get; } = id;
        public Room Room { get; } = room;
        public string Hash { get; } = hash;
        public string Name { get; } = name;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public Member Uploader { get; } = uploader;
        public Member Downloader { get; } = downloader;
        public string UploadToken { get; } = uploadToken;
        public string DownloadToken { get; } = downloadToken;
        public CancellationTokenSource Cts { get; } = new();
        public TransferPhase Phase { get; set; } = TransferPhase.Upload;
        public bool UploadStarted { get; set; }
        public long Bytes { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        public DateTimeOffset LastProgressSent { get; set; }
        public string? FilePath { get; set; }
        /// <summary>The file is not the room's cached copy and is deleted when the transfer ends.</summary>
        public bool OwnsFile { get; set; }
    }
}
