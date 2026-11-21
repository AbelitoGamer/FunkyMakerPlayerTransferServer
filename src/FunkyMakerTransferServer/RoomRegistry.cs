using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FunkyMakerTransferServer;

/// <summary>A message to send to one connection after a registry call.</summary>
public sealed record Outbound(string ConnectionId, string Method, object Payload);

/// <summary>
/// Side effects collected while the registry lock is held and executed after it is released
/// (by <see cref="EffectRunner"/>): hub messages, cancelling running HTTP streams, deleting files,
/// recording finished rounds in the leaderboard.
/// </summary>
public sealed class Effects
{
    public List<Outbound> Messages { get; } = new();
    public List<CancellationTokenSource> ToCancel { get; } = new();
    public List<string> FilesToDelete { get; } = new();
    public List<RoundResult> RoundsToRecord { get; } = new();

    internal void Send(string connectionId, string method, object payload) =>
        Messages.Add(new Outbound(connectionId, method, payload));
}

public sealed record UploadTicket(string TransferId, string Hash, long Size, CancellationToken Token);
public sealed record DownloadTicket(string TransferId, string FilePath, long Size, string FileName, CancellationToken Token);
public sealed record RegistryStats(int Sessions, int Rooms, int Members, int Matches, int Transfers, int CachedFiles);

/// <summary>Official levels known to the server (verified_levels), looked up by SHA-256.</summary>
public interface IOfficialLevels
{
    OfficialLevel? FindByHash(string hash);
}

public sealed record OfficialLevel(string Id, string Name, string FileName, string Hash, long Size);

/// <summary>
/// All session, room, round, match and level-transfer state. Every public method takes the single
/// lock, so the state is always consistent; network I/O happens outside it through <see cref="Effects"/>.
///
/// Round flow: the player whose turn it is (or the host in host-only rooms) picks a song → every
/// player who lacks it gets it (official: from /api/verified_levels; local: one holder uploads it once,
/// everybody else downloads it) → players mark themselves ready (the picker is ready automatically) →
/// when every non-spectator is ready the match loads → when every player has loaded, the server sends
/// a synced start time → each player reports their final result → the server ranks them, records the
/// round in the leaderboard and passes the turn on.
/// </summary>
public sealed partial class RoomRegistry
{
    public static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FinishGrace = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KickBan = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ChatInterval = TimeSpan.FromMilliseconds(300);
    public const int MaxChatHistory = 60;
    public const int MaxRoundHistory = 50;
    /// <summary>Countdown the clients show between "everybody loaded" and the song start (4 beats at 60 BPM).</summary>
    public const int CountdownMs = 4000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly TransferOptions _options;
    private readonly TimeProvider _time;
    private readonly IOfficialLevels _official;
    private long _joinSeq;

    public RoomRegistry(IOptions<TransferOptions> options, TimeProvider time, IOfficialLevels official)
    {
        _options = options.Value;
        _time = time;
        _official = official;
    }

    public long MaxUploadBytes => _options.MaxUploadBytes;

    /// <summary>Picks random values (team split, sides, ties). Tests replace it for determinism.</summary>
    internal Func<int, int> RandomInt { get; set; } = max => RandomNumberGenerator.GetInt32(max);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    // ══════════════════════════════════════════════════════════════════════
    // Sessions
    // ══════════════════════════════════════════════════════════════════════

    public HelloResult Hello(string connectionId, HelloRequest request, Effects fx)
    {
        if (request.ProtocolVersion != Protocol.Version)
        {
            return new HelloResult
            {
                Ok = false,
                Error = $"Online protocol mismatch (server {Protocol.Version}, game {request.ProtocolVersion}). Update the game.",
            };
        }

        lock (_lock)
        {
            string playerId = Clean(request.PlayerId, 64);
            if (playerId.Length == 0) playerId = "conn-" + connectionId;
            if (!_sessions.TryGetValue(connectionId, out var s))
            {
                s = new Session(connectionId);
                _sessions[connectionId] = s;
            }
            s.PlayerId = playerId;
            s.Username = CleanName(request.Username, "Player");
            s.ColorHue = Math.Clamp(request.ColorHue, 0, 360);
            s.Greeted = true;
            // Profile changes show up in the room right away.
            if (s.Member != null) BroadcastRoomLocked(s.Member.Room, fx);
        }
        return new HelloResult { Ok = true, MaxUploadBytes = _options.MaxUploadBytes };
    }

    /// <summary>Updates the connection's average ping (used for the start buffer) and returns the reply.</summary>
    public PongPacket Ping(string connectionId, long clientTimestamp)
    {
        long now = NowMs;
        int sample = (int)Math.Clamp(now - clientTimestamp, 0, 5000);
        lock (_lock)
        {
            if (_sessions.TryGetValue(connectionId, out var s))
                s.AvgPing = s.AvgPing <= 0 ? sample : s.AvgPing * 0.7 + sample * 0.3;
        }
        return new PongPacket { ClientTimestamp = clientTimestamp, ServerTimestamp = now, Ping = sample };
    }

    public void Disconnect(string connectionId, Effects fx)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(connectionId, out var s)) return;
            if (s.Member != null) RemoveMemberLocked(s.Member, fx, $"{s.Username} disconnected.");
            _sessions.Remove(connectionId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Room browser
    // ══════════════════════════════════════════════════════════════════════

    public List<RoomSummary> ListRooms()
    {
        lock (_lock)
        {
            return _rooms.Values
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new RoomSummary
                {
                    Id = r.Id,
                    Name = r.Name,
                    HasPassword = r.Settings.Password.Length > 0,
                    Mode = r.Settings.Mode,
                    Phase = r.Phase,
                    SongName = r.Match != null ? r.Match.Info.Song.Name : "",
                    SongStars = r.Match != null ? r.Match.Info.Song.Stars : 0f,
                    HostName = r.Host?.Session.Username ?? "",
                    Players = r.Members.Select(m => new RoomPlayerSummary
                    {
                        Id = m.Session.PlayerId, Username = m.Session.Username, ColorHue = m.Session.ColorHue,
                    }).ToList(),
                    PlayerCount = r.Members.Count,
                    MaxPlayers = r.Settings.MaxPlayers,
                })
                .ToList();
        }
    }

    public OpResult CreateRoom(string connectionId, CreateRoomRequest request, Effects fx)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(connectionId, out var s) || !s.Greeted)
                return OpResult.Fail("Not connected.");

            if (s.Member != null) RemoveMemberLocked(s.Member, fx, $"{s.Username} left.");

            var settings = SanitizeSettings(request.Settings, minPlayers: Protocol.MinPlayers, out string? error);
            if (settings == null) return OpResult.Fail(error!);

            string name = Clean(request.Name, Protocol.MaxRoomNameLength);
            if (name.Length == 0) name = Clean($"{s.Username}'s room", Protocol.MaxRoomNameLength);

            var room = new Room(NewRoomId(), name, settings, Now);
            _rooms[room.Id] = room;
            var member = AddMemberLocked(room, s, request.Levels);
            room.TurnSeq = member.JoinSeq;
            AddSystemChatLocked(room, $"{s.Username} created the room.", fx);
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    public OpResult JoinRoom(string connectionId, JoinRoomRequest request, Effects fx)
    {
        string roomId = (request.RoomId ?? "").Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (!_sessions.TryGetValue(connectionId, out var s) || !s.Greeted)
                return OpResult.Fail("Not connected.");
            if (!_rooms.TryGetValue(roomId, out var room))
                return OpResult.Fail("That room doesn't exist any more.");
            if (s.Member?.Room == room)
                return OpResult.Success(room.Id);

            // Same player on a new connection (reconnect): take over their place in the room.
            var stale = room.Members.FirstOrDefault(m => m.Session.PlayerId == s.PlayerId);
            if (stale != null)
            {
                if (s.Member != null) RemoveMemberLocked(s.Member, fx, $"{s.Username} left.");
                var old = stale.Session;
                old.Member = null;
                fx.Send(old.ConnectionId, Protocol.Kicked, new KickedPacket { Reason = "You joined this room from another window." });
                _sessions.Remove(old.ConnectionId);
                stale.Session = s;
                s.Member = stale;
                stale.Levels = SanitizeLevels(request.Levels);
                stale.SentLevelsVersion = 0;
                stale.SentHistoryVersion = 0;
                room.LevelsVersion++;
                UpdateMemberLevelStateLocked(stale);
                SendChatHistoryLocked(room, s, fx);
                BroadcastRoomLocked(room, fx);
                return OpResult.Success(room.Id);
            }

            if (room.Bans.TryGetValue(s.PlayerId, out var until) && until > Now)
                return OpResult.Fail("You were kicked from this room. Try again later.");
            if (room.Members.Count >= room.Settings.MaxPlayers)
                return OpResult.Fail("This room is full.");
            if (room.Settings.Password.Length > 0 && !SecretEquals(room.Settings.Password, request.Password))
                return OpResult.Fail("Wrong password.");

            if (s.Member != null) RemoveMemberLocked(s.Member, fx, $"{s.Username} left.");
            var member = AddMemberLocked(room, s, request.Levels);
            SendChatHistoryLocked(room, s, fx);
            AddSystemChatLocked(room, $"{s.Username} joined.", fx);
            EnsureTurnLocked(room);
            OfferDownloadLocked(room, member, fx);
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    public void LeaveRoom(string connectionId, Effects fx)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(connectionId, out var s) && s.Member != null)
                RemoveMemberLocked(s.Member, fx, $"{s.Username} left.");
        }
    }

    public void UpdateLevels(string connectionId, List<LevelEntry>? levels, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            m.Levels = SanitizeLevels(levels);
            m.Room.LevelsVersion++;
            UpdateMemberLevelStateLocked(m);
            TryAdvanceRoundLocked(m.Room, fx);
            BroadcastRoomLocked(m.Room, fx);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Host tools
    // ══════════════════════════════════════════════════════════════════════

    public OpResult UpdateSettings(string connectionId, RoomSettings request, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return OpResult.Fail("You're not in a room.");
            var room = m.Room;
            if (room.Host != m) return OpResult.Fail("Only the host can change the room settings.");
            if (room.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying)
                return OpResult.Fail("Settings can only be changed between rounds.");

            var next = SanitizeSettings(request, minPlayers: Math.Max(Protocol.MinPlayers, room.Members.Count), out string? error);
            if (next == null) return OpResult.Fail(error!);

            var old = room.Settings;
            var changes = new List<string>();
            if (old.Mode != next.Mode) changes.Add($"mode → {ModeName(next.Mode)}");
            if (old.SongSelection != next.SongSelection) changes.Add($"song selection → {SelectionName(next.SongSelection)}");
            if (old.MaxPlayers != next.MaxPlayers) changes.Add($"player limit → {next.MaxPlayers}");
            if ((old.Password.Length > 0) != (next.Password.Length > 0))
                changes.Add(next.Password.Length > 0 ? "room is now password protected" : "room is now open");
            else if (old.Password != next.Password)
                changes.Add("password changed");
            if (old.AllowGuestLocalLevels != next.AllowGuestLocalLevels)
                changes.Add(next.AllowGuestLocalLevels ? "guests may pick their local levels" : "guests may only pick official / host levels");

            if (changes.Count == 0) return OpResult.Success(room.Id);

            room.Settings = next;
            room.LevelsVersion++; // pickability may have changed
            AddSystemChatLocked(room, $"Host changed the settings: {string.Join(", ", changes)}.", fx);

            if (old.SongSelection != next.SongSelection && next.SongSelection == Protocol.SelectHost && room.Host != null)
                room.TurnSeq = room.Host.JoinSeq;
            EnsureTurnLocked(room);

            if (room.Song != null)
            {
                bool stillAllowed = room.Song.Source == Protocol.SourceOfficial || IsPickableLocked(room, room.Song.Hash);
                bool chooserStillTurn = room.TurnMember?.Session.PlayerId == room.Song.PickedBy;
                if (!stillAllowed || !chooserStillTurn)
                {
                    ClearSongLocked(room, fx);
                    AddSystemChatLocked(room, "The picked song was cleared by the new settings.", fx);
                }
                else if (old.Mode != next.Mode)
                {
                    ResetReadyLocked(room, keepPicker: true);
                }
            }

            TryAdvanceRoundLocked(room, fx);
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    public OpResult Kick(string connectionId, string playerId, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return OpResult.Fail("You're not in a room.");
            var room = m.Room;
            if (room.Host != m) return OpResult.Fail("Only the host can kick players.");
            var target = room.Members.FirstOrDefault(x => x.Session.PlayerId == playerId);
            if (target == null) return OpResult.Fail("That player isn't in the room.");
            if (target == m) return OpResult.Fail("You can't kick yourself.");

            room.Bans[target.Session.PlayerId] = Now + KickBan;
            fx.Send(target.Session.ConnectionId, Protocol.Kicked, new KickedPacket { Reason = "The host removed you from the room." });
            RemoveMemberLocked(target, fx, $"{target.Session.Username} was kicked by the host.");
            return OpResult.Success(room.Id);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Chat & player state in the waiting room
    // ══════════════════════════════════════════════════════════════════════

    public void Chat(string connectionId, string? text, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            string clean = Clean(text, Protocol.MaxChatLength);
            if (clean.Length == 0) return;
            var now = Now;
            if (now - m.LastChat < ChatInterval) return;
            m.LastChat = now;
            if (clean.Equals("/forcestart", StringComparison.OrdinalIgnoreCase))
            {
                ForceStartLocked(m, fx);
                return;
            }
            AddChatLocked(m.Room, new ChatMessage
            {
                PlayerId = m.Session.PlayerId,
                Username = m.Session.Username,
                ColorHue = m.Session.ColorHue,
                Text = clean,
                Timestamp = NowMs,
            }, fx);
        }
    }

    /// <summary>
    /// Host chat command "/forcestart": marks every playing member who has the level as ready,
    /// then starts the round if nothing else blocks it (workaround for clients whose Ready
    /// button doesn't work). Players still need the level; the reason is posted if it can't start.
    /// </summary>
    private void ForceStartLocked(Member m, Effects fx)
    {
        var room = m.Room;
        if (room.Host != m)
        {
            AddSystemChatLocked(room, "Only the host can use /forcestart.", fx);
            return;
        }
        if (room.Phase != Protocol.PhaseSongSelected || room.Song == null)
        {
            AddSystemChatLocked(room, "/forcestart: no song is waiting to start.", fx);
            return;
        }

        foreach (var p in ActivePlayers(room))
            if (p.LevelState == Protocol.LevelReady) p.Ready = true;

        string? blocker = StartBlockerLocked(room);
        if (blocker != null)
        {
            AddSystemChatLocked(room, $"/forcestart: can't start yet. {blocker}.", fx);
            BroadcastRoomLocked(room, fx);
            return;
        }

        AddSystemChatLocked(room, $"{m.Session.Username} force-started the round.", fx);
        TryAdvanceRoundLocked(room, fx);
        BroadcastRoomLocked(room, fx);
    }

    public void SetSpectate(string connectionId, bool spectate, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m) || m.Spectate == spectate) return;
            var room = m.Room;
            m.Spectate = spectate;
            m.Ready = false;
            m.Vote = -1;
            AddSystemChatLocked(room, spectate ? $"{m.Session.Username} will spectate." : $"{m.Session.Username} will play.", fx);

            // A spectating picker gives the turn away (turn rooms only).
            if (spectate && room.Settings.SongSelection == Protocol.SelectTurns && room.TurnMember == m
                && room.Phase is Protocol.PhaseLobby or Protocol.PhaseSongSelected)
            {
                if (room.Song != null) ClearSongLocked(room, fx);
                AdvanceTurnLocked(room);
                AnnounceTurnLocked(room, fx);
            }
            else
            {
                EnsureTurnLocked(room);
            }
            TryAdvanceRoundLocked(room, fx);
            BroadcastRoomLocked(room, fx);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Round: pick, ready, vote
    // ══════════════════════════════════════════════════════════════════════

    public OpResult SelectSong(string connectionId, SongSelection selection, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return OpResult.Fail("You're not in a room.");
            var room = m.Room;
            if (room.Phase is not (Protocol.PhaseLobby or Protocol.PhaseSongSelected))
                return OpResult.Fail("Wait for the current round to end.");
            EnsureTurnLocked(room);
            if (room.TurnMember != m) return OpResult.Fail("It's not your turn to pick.");

            string hash = (selection.Hash ?? "").Trim().ToLowerInvariant();
            if (!HashRegex().IsMatch(hash)) return OpResult.Fail("Invalid level.");

            SongInfo song;
            if (selection.Source == Protocol.SourceOfficial)
            {
                var off = _official.FindByHash(hash);
                if (off == null) return OpResult.Fail("That official level isn't on the server.");
                song = new SongInfo
                {
                    Source = Protocol.SourceOfficial, Hash = hash, Name = off.Name, FileName = off.FileName,
                    Size = off.Size, OfficialId = off.Id,
                };
            }
            else if (selection.Source == Protocol.SourceLocal)
            {
                var entry = room.Members.Select(x => x.Levels.GetValueOrDefault(hash)).FirstOrDefault(e => e != null);
                if (entry == null) return OpResult.Fail("Nobody in the room has that level any more.");
                if (!IsPickableLocked(room, hash))
                    return OpResult.Fail("This room only allows official levels and the host's levels.");
                song = new SongInfo
                {
                    Source = Protocol.SourceLocal, Hash = hash, Name = entry.Name, FileName = entry.FileName, Size = entry.Size,
                };
            }
            else
            {
                return OpResult.Fail("Unknown level source.");
            }

            song.Stars = Math.Clamp(selection.Stars, 0f, 99f);
            song.PickedBy = m.Session.PlayerId;

            if (room.Song != null) ClearSongLocked(room, fx);
            room.Song = song;
            room.Phase = Protocol.PhaseSongSelected;
            ResetReadyLocked(room, keepPicker: false);
            m.Ready = !m.Spectate;
            foreach (var x in room.Members) UpdateMemberLevelStateLocked(x);

            string stars = song.Stars > 0 ? $" (★{song.Stars:0.0})" : "";
            AddSystemChatLocked(room, $"{m.Session.Username} picked {song.Name}{stars}.", fx);

            if (song.Source == Protocol.SourceLocal) StartTransferLocked(room, fx);
            TryAdvanceRoundLocked(room, fx);
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    public OpResult SetReady(string connectionId, bool ready, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return OpResult.Fail("You're not in a room.");
            var room = m.Room;
            if (room.Phase != Protocol.PhaseSongSelected) return OpResult.Fail("No song is waiting to start.");
            if (m.Spectate) return OpResult.Fail("Spectators don't need to be ready.");
            if (ready && m.LevelState != Protocol.LevelReady) return OpResult.Fail("You don't have the level yet.");
            if (m.Ready == ready) return OpResult.Success(room.Id);
            m.Ready = ready;
            TryAdvanceRoundLocked(room, fx);
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    public void SetLevelStatus(string connectionId, LevelStatusUpdate update, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            var room = m.Room;
            var song = room.Song ?? room.Match?.Info.Song;
            if (song == null || !string.Equals(song.Hash, update.Hash, StringComparison.OrdinalIgnoreCase)) return;

            string state = update.State switch
            {
                Protocol.LevelReady => Protocol.LevelReady,
                Protocol.LevelDownloading => Protocol.LevelDownloading,
                Protocol.LevelFailed => Protocol.LevelFailed,
                _ => Protocol.LevelMissing,
            };
            // The server knows best for local levels it is sending itself.
            if (state == Protocol.LevelReady && song.Source == Protocol.SourceLocal && !m.Levels.ContainsKey(song.Hash))
                m.Room.LevelsVersion++;
            if (state == Protocol.LevelReady && song.Source == Protocol.SourceLocal)
                m.Levels.TryAdd(song.Hash, new LevelEntry { Hash = song.Hash, Name = song.Name, FileName = song.FileName, Size = song.Size });

            bool changed = m.LevelState != state;
            m.LevelState = state;
            m.LevelProgress = Math.Clamp(update.Progress, 0f, 1f);
            if (state != Protocol.LevelReady) m.Ready = false;

            // "missing" on a local song = please send it (again).
            if (state == Protocol.LevelMissing && song.Source == Protocol.SourceLocal)
                OfferDownloadLocked(room, m, fx);

            if (changed) TryAdvanceRoundLocked(room, fx);
            if (changed || ProgressBroadcastDue(room)) BroadcastRoomLocked(room, fx);
        }
    }

    public OpResult VoteSide(string connectionId, int side, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return OpResult.Fail("You're not in a room.");
            var room = m.Room;
            if (room.Settings.Mode != Protocol.ModeMirror) return OpResult.Fail("Votes are only used in Mirror mode.");
            if (room.Phase != Protocol.PhaseSongSelected) return OpResult.Fail("No song is waiting to start.");
            if (m.Spectate) return OpResult.Fail("Spectators don't vote.");
            m.Vote = side is Protocol.SidePlayer or Protocol.SideEnemy ? side : -1;
            BroadcastRoomLocked(room, fx);
            return OpResult.Success(room.Id);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Match
    // ══════════════════════════════════════════════════════════════════════

    public void MatchLoaded(string connectionId, string matchId, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            var room = m.Room;
            var match = room.Match;
            if (match == null || match.Info.MatchId != matchId || room.Phase != Protocol.PhaseLoading) return;
            if (!match.Participants.ContainsKey(m.Session.PlayerId)) return;
            match.Loaded.Add(m.Session.PlayerId);
            TryStartMatchLocked(room, fx);
        }
    }

    /// <summary>Relays an in-match packet from a player to everybody else in the room.</summary>
    public bool Relay(string connectionId, out string playerId, out List<string> targets)
    {
        playerId = "";
        targets = new List<string>();
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return false;
            var room = m.Room;
            if (room.Match == null || !room.Match.Participants.ContainsKey(m.Session.PlayerId)) return false;
            playerId = m.Session.PlayerId;
            foreach (var o in room.Members)
                if (o != m) targets.Add(o.Session.ConnectionId);
            return true;
        }
    }

    /// <summary>Remembers the latest live state (used when a player never sends a final result).</summary>
    public void RecordState(string connectionId, PlayerStatePacket state)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m) || m.Room.Match == null) return;
            if (m.Room.Match.Participants.TryGetValue(m.Session.PlayerId, out var p) && p.Final == null)
                p.Last = state;
        }
    }

    public void FinishMatch(string connectionId, FinishMatchRequest request, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            var room = m.Room;
            var match = room.Match;
            if (match == null || match.Info.MatchId != request.MatchId || room.Phase != Protocol.PhasePlaying) return;
            if (!match.Participants.TryGetValue(m.Session.PlayerId, out var p) || p.Final != null) return;
            p.Final = request;
            if (request.Quit)
            {
                p.Gone = true;
                AddSystemChatLocked(room, $"{m.Session.Username} left the match.", fx);
            }
            else
            {
                match.FirstFinishAt ??= Now;
            }
            if (match.Participants.Values.All(x => x.Final != null || x.Gone))
                FinishRoundLocked(room, fx);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Transfers (HTTP side)
    // ══════════════════════════════════════════════════════════════════════

    public UploadTicket? BeginUpload(string transferId, string token, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || !SecretEquals(t.UploadToken, token))
            {
                error = "Unknown transfer.";
                return null;
            }
            if (t.FilePath != null || t.UploadStarted)
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

    public void ReportUploadBytes(string transferId, long bytes, Effects fx)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.FilePath != null) return;
            t.UploadBytes = bytes;
            t.LastActivity = Now;
            var uploader = t.Room.Members.FirstOrDefault(x => x.Session.PlayerId == t.UploaderId);
            if (uploader != null) uploader.UploadProgress = t.Size > 0 ? (float)bytes / t.Size : 0f;
            if (ProgressBroadcastDue(t.Room)) BroadcastRoomLocked(t.Room, fx);
        }
    }

    /// <summary>The upload finished; checks it, caches it for the room and starts every download.</summary>
    public bool CompleteUpload(string transferId, string path, string actualHash, long actualSize, Effects fx, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.FilePath != null)
            {
                fx.FilesToDelete.Add(path);
                error = "The transfer was cancelled.";
                return false;
            }
            if (!string.Equals(actualHash, t.Hash, StringComparison.OrdinalIgnoreCase) || actualSize != t.Size)
            {
                fx.FilesToDelete.Add(path);
                FailTransferLocked(t, fx, "The uploaded file did not match the level (it may have changed on disk).", tryNextHolder: true);
                error = "Uploaded file does not match the announced hash/size.";
                return false;
            }

            t.FilePath = path;
            t.LastActivity = Now;
            if (t.Room.Cache.TryGetValue(t.Hash, out var existing))
            {
                fx.FilesToDelete.Add(existing.Path);
            }
            t.Room.Cache[t.Hash] = new CachedFile(path, t.Size) { ExpiresAt = Now.AddMinutes(_options.CacheMinutes) };
            foreach (var u in t.Room.Members) u.UploadProgress = -1f;
            foreach (var m in t.Room.Members) OfferDownloadLocked(t.Room, m, fx);
            BroadcastRoomLocked(t.Room, fx);
            error = "";
            return true;
        }
    }

    public void FailUpload(string transferId, string reason, string path, Effects fx)
    {
        fx.FilesToDelete.Add(path);
        lock (_lock)
        {
            if (_transfers.TryGetValue(transferId, out var t) && t.FilePath == null)
                FailTransferLocked(t, fx, reason, tryNextHolder: true);
        }
    }

    public DownloadTicket? BeginDownload(string transferId, string token, out string error)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t) || t.FilePath == null)
            {
                error = "This transfer is not ready for download.";
                return null;
            }
            var d = t.Downloads.Values.FirstOrDefault(x => SecretEquals(x.Token, token));
            if (d == null)
            {
                error = "Unknown transfer.";
                return null;
            }
            d.LastActivity = Now;
            d.Bytes = 0;
            if (t.Room.Cache.TryGetValue(t.Hash, out var cached)) cached.ExpiresAt = Now.AddMinutes(_options.CacheMinutes);
            error = "";
            return new DownloadTicket(t.Id, t.FilePath, t.Size, t.FileName, d.Cts.Token);
        }
    }

    public void ReportDownloadBytes(string transferId, string token, long bytes, Effects fx)
    {
        lock (_lock)
        {
            if (!_transfers.TryGetValue(transferId, out var t)) return;
            var d = t.Downloads.Values.FirstOrDefault(x => x.Token == token);
            if (d == null) return;
            d.Bytes = bytes;
            d.LastActivity = Now;
            var m = t.Room.Members.FirstOrDefault(x => x.Session.PlayerId == d.PlayerId);
            if (m != null && m.LevelState != Protocol.LevelReady)
            {
                m.LevelState = Protocol.LevelDownloading;
                m.LevelProgress = t.Size > 0 ? Math.Min(1f, (float)bytes / t.Size) : 0f;
            }
            if (ProgressBroadcastDue(t.Room)) BroadcastRoomLocked(t.Room, fx);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Transfers (hub side)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The downloader reports whether the saved file matched the expected hash.</summary>
    public void CompleteDownload(string connectionId, string transferId, bool ok, string? error, Effects fx)
    {
        lock (_lock)
        {
            if (!TryMember(connectionId, out var m)) return;
            if (!_transfers.TryGetValue(transferId, out var t) || !t.Downloads.Remove(m.Session.PlayerId, out var d)) return;
            fx.ToCancel.Add(d.Cts);
            if (ok)
            {
                m.Levels[t.Hash] = new LevelEntry { Hash = t.Hash, Name = t.Name, FileName = t.FileName, Size = t.Size };
                m.Room.LevelsVersion++;
                UpdateMemberLevelStateLocked(m);
            }
            else
            {
                m.LevelState = Protocol.LevelFailed;
                m.LevelProgress = 0f;
                fx.Send(m.Session.ConnectionId, Protocol.TransferFailed, new TransferFailedPacket
                {
                    Hash = t.Hash, Reason = $"Download failed: {(string.IsNullOrWhiteSpace(error) ? "verification failed" : Clean(error, 160))}",
                });
            }
            TryAdvanceRoundLocked(m.Room, fx);
            BroadcastRoomLocked(m.Room, fx);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Maintenance
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Timeouts (stalled transfers, slow loaders, players who never finish) and cache expiry.</summary>
    public void Sweep(Effects fx)
    {
        lock (_lock)
        {
            var now = Now;
            var stall = TimeSpan.FromSeconds(_options.StallTimeoutSeconds);

            foreach (var t in _transfers.Values.ToList())
            {
                if (t.FilePath == null)
                {
                    if (now - t.LastActivity > stall)
                        FailTransferLocked(t, fx, "The level upload timed out.", tryNextHolder: true);
                    continue;
                }
                foreach (var d in t.Downloads.Values.Where(d => now - d.LastActivity > stall).ToList())
                {
                    t.Downloads.Remove(d.PlayerId);
                    fx.ToCancel.Add(d.Cts);
                    var m = t.Room.Members.FirstOrDefault(x => x.Session.PlayerId == d.PlayerId);
                    if (m == null) continue;
                    m.LevelState = Protocol.LevelFailed;
                    fx.Send(m.Session.ConnectionId, Protocol.TransferFailed, new TransferFailedPacket { Hash = t.Hash, Reason = "The download timed out." });
                    BroadcastRoomLocked(t.Room, fx);
                }
            }

            foreach (var room in _rooms.Values.ToList())
            {
                var match = room.Match;
                if (match != null && room.Phase == Protocol.PhaseLoading && now - match.CreatedAt > LoadingTimeout)
                {
                    var slow = match.Participants.Keys.Where(id => !match.Loaded.Contains(id)).ToList();
                    foreach (var id in slow) match.Participants.Remove(id);
                    var names = room.Members.Where(x => slow.Contains(x.Session.PlayerId)).Select(x => x.Session.Username);
                    AddSystemChatLocked(room, $"{string.Join(", ", names)} took too long to load and will spectate.", fx);
                    if (!TryStartMatchLocked(room, fx))
                        CancelMatchLocked(room, fx, "Not enough players loaded the level. Round cancelled.");
                }
                else if (match != null && room.Phase == Protocol.PhasePlaying && match.FirstFinishAt is { } first && now - first > FinishGrace)
                {
                    FinishRoundLocked(room, fx);
                }

                foreach (var (hash, file) in room.Cache.ToList())
                {
                    if (file.ExpiresAt > now) continue;
                    if (room.Song?.Hash == hash || room.Match?.Info.Song.Hash == hash) continue;
                    if (_transfers.Values.Any(t => t.FilePath == file.Path)) continue;
                    room.Cache.Remove(hash);
                    fx.FilesToDelete.Add(file.Path);
                }

                foreach (var (id, until) in room.Bans.ToList())
                    if (until <= now) room.Bans.Remove(id);
            }
        }
    }

    public RegistryStats GetStats()
    {
        lock (_lock)
        {
            return new RegistryStats(_sessions.Count, _rooms.Count, _rooms.Values.Sum(r => r.Members.Count),
                _rooms.Values.Count(r => r.Match != null), _transfers.Count, _rooms.Values.Sum(r => r.Cache.Count));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: membership
    // ══════════════════════════════════════════════════════════════════════

    private DateTimeOffset Now => _time.GetUtcNow();
    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private bool TryMember(string connectionId, out Member member)
    {
        if (_sessions.TryGetValue(connectionId, out var s) && s.Member != null)
        {
            member = s.Member;
            return true;
        }
        member = null!;
        return false;
    }

    private Member AddMemberLocked(Room room, Session s, List<LevelEntry>? levels)
    {
        var member = new Member(s, room, ++_joinSeq) { Levels = SanitizeLevels(levels) };
        room.LevelsVersion++;
        room.Members.Add(member);
        s.Member = member;
        UpdateMemberLevelStateLocked(member);
        return member;
    }

    private void RemoveMemberLocked(Member member, Effects fx, string chatLine)
    {
        var room = member.Room;
        bool wasHost = room.Host == member;
        bool wasTurn = room.TurnMember == member;
        string playerId = member.Session.PlayerId;

        room.Members.Remove(member);
        member.Session.Member = null;
        room.LevelsVersion++;

        if (room.Members.Count == 0)
        {
            foreach (var t in _transfers.Values.Where(t => t.Room == room).ToList()) EndTransferLocked(t, fx);
            _rooms.Remove(room.Id);
            foreach (var file in room.Cache.Values) fx.FilesToDelete.Add(file.Path);
            room.Cache.Clear();
            return;
        }

        AddSystemChatLocked(room, chatLine, fx);
        if (wasHost && room.Host != null)
            AddSystemChatLocked(room, $"{room.Host.Session.Username} is the new host.", fx);

        // Their level download / upload.
        foreach (var t in _transfers.Values.Where(t => t.Room == room).ToList())
        {
            if (t.Downloads.Remove(playerId, out var d)) fx.ToCancel.Add(d.Cts);
            if (t.FilePath == null && t.UploaderId == playerId)
                FailTransferLocked(t, fx, $"{member.Session.Username} left before sending the level.", tryNextHolder: true);
        }

        // Their match.
        if (room.Match != null && room.Match.Participants.TryGetValue(playerId, out var p))
        {
            if (room.Phase == Protocol.PhaseLoading)
            {
                room.Match.Participants.Remove(playerId);
                if (!TryStartMatchLocked(room, fx) && room.Match.Participants.Count < MinParticipants(room.Match.Info.Mode))
                    CancelMatchLocked(room, fx, "Not enough players left. Round cancelled.");
            }
            else
            {
                p.Gone = true;
                AddSystemChatLocked(room, $"{member.Session.Username} left the match.", fx);
                if (room.Match.Participants.Values.All(x => x.Final != null || x.Gone))
                    FinishRoundLocked(room, fx);
            }
        }

        // Their song pick / turn.
        if (room.Phase is Protocol.PhaseLobby or Protocol.PhaseSongSelected)
        {
            if (room.Song != null && room.Song.PickedBy == playerId)
            {
                ClearSongLocked(room, fx);
                AddSystemChatLocked(room, "Their song pick was cancelled.", fx);
            }
            if (wasTurn || wasHost)
            {
                if (room.Settings.SongSelection == Protocol.SelectHost && room.Host != null)
                    room.TurnSeq = room.Host.JoinSeq;
                EnsureTurnLocked(room);
                AnnounceTurnLocked(room, fx);
            }
            else
            {
                EnsureTurnLocked(room);
            }
        }

        TryAdvanceRoundLocked(room, fx);
        BroadcastRoomLocked(room, fx);
    }

    private void UpdateMemberLevelStateLocked(Member m)
    {
        var song = m.Room.Song ?? m.Room.Match?.Info.Song;
        if (song == null)
        {
            m.LevelState = Protocol.LevelMissing;
            m.LevelProgress = 0f;
            return;
        }
        if (m.Levels.ContainsKey(song.Hash))
        {
            m.LevelState = Protocol.LevelReady;
            m.LevelProgress = 1f;
        }
        else if (m.LevelState == Protocol.LevelReady && song.Source == Protocol.SourceLocal)
        {
            // Their level list no longer has it.
            m.LevelState = Protocol.LevelMissing;
            m.LevelProgress = 0f;
            m.Ready = false;
        }
    }

    private bool IsPickableLocked(Room room, string hash)
    {
        if (room.Settings.AllowGuestLocalLevels)
            return room.Members.Any(x => x.Levels.ContainsKey(hash));
        return room.Host != null && room.Host.Levels.ContainsKey(hash);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: turns
    // ══════════════════════════════════════════════════════════════════════

    private static bool CanPick(Member m) => !m.Spectate;

    /// <summary>Makes sure TurnSeq points at somebody who can pick (keeping the current picker when possible).</summary>
    private void EnsureTurnLocked(Room room)
    {
        if (room.Members.Count == 0) return;
        if (room.Settings.SongSelection == Protocol.SelectHost)
        {
            room.TurnSeq = room.Host!.JoinSeq;
            return;
        }
        var current = room.Members.FirstOrDefault(m => m.JoinSeq == room.TurnSeq);
        if (current != null && CanPick(current)) return;
        // The next eligible player after the old position (join order, wrapping).
        var next = NextEligibleAfter(room, room.TurnSeq, inclusive: true);
        room.TurnSeq = next?.JoinSeq ?? room.Host!.JoinSeq;
    }

    private void AdvanceTurnLocked(Room room)
    {
        if (room.Members.Count == 0) return;
        if (room.Settings.SongSelection == Protocol.SelectHost)
        {
            room.TurnSeq = room.Host!.JoinSeq;
            return;
        }
        var next = NextEligibleAfter(room, room.TurnSeq, inclusive: false);
        room.TurnSeq = next?.JoinSeq ?? room.Host!.JoinSeq;
    }

    private static Member? NextEligibleAfter(Room room, long seq, bool inclusive)
    {
        var ordered = room.Members; // join order
        var after = ordered.Where(m => inclusive ? m.JoinSeq >= seq : m.JoinSeq > seq).Where(CanPick);
        return after.FirstOrDefault() ?? ordered.Where(CanPick).FirstOrDefault();
    }

    private void AnnounceTurnLocked(Room room, Effects fx)
    {
        var t = room.TurnMember;
        if (t != null && CanPick(t) && room.Members.Count > 1)
            AddSystemChatLocked(room, $"It's {t.Session.Username}'s turn to pick a song.", fx);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: round state machine
    // ══════════════════════════════════════════════════════════════════════

    private static int MinParticipants(string mode) => 2;

    private List<Member> ActivePlayers(Room room) => room.Members.Where(m => !m.Spectate).ToList();

    /// <summary>Why the selected song can't start yet, or null when it can.</summary>
    private string? StartBlockerLocked(Room room)
    {
        if (room.Phase != Protocol.PhaseSongSelected || room.Song == null) return null;
        var players = ActivePlayers(room);
        if (players.Count < 2) return "Waiting for more players";
        if (room.Settings.Mode == Protocol.ModeVersus && players.Count != 2)
            return "Versus needs exactly 2 players (the rest can spectate)";
        var noLevel = players.Where(p => p.LevelState != Protocol.LevelReady).ToList();
        if (noLevel.Count > 0) return $"Waiting for {Names(noLevel)} to get the level";
        var notReady = players.Where(p => !p.Ready).ToList();
        if (notReady.Count > 0) return $"Waiting for {Names(notReady)} to be ready";
        return null;
    }

    private static string Names(List<Member> list) =>
        list.Count <= 2 ? string.Join(" and ", list.Select(m => m.Session.Username)) : $"{list.Count} players";

    /// <summary>Starts loading the match when everybody is ready.</summary>
    private void TryAdvanceRoundLocked(Room room, Effects fx)
    {
        if (room.Phase != Protocol.PhaseSongSelected || room.Song == null) return;
        if (StartBlockerLocked(room) != null) return;

        var players = ActivePlayers(room);
        var info = new MatchInfo
        {
            MatchId = Guid.NewGuid().ToString("N"),
            Mode = room.Settings.Mode,
            Song = room.Song,
        };
        var shuffled = Shuffle(players);
        switch (room.Settings.Mode)
        {
            case Protocol.ModeTeams:
            {
                // Random split; with an odd count a random team gets the extra player.
                int team0 = shuffled.Count / 2 + (shuffled.Count % 2 == 1 ? RandomInt(2) : 0);
                for (int i = 0; i < shuffled.Count; i++)
                {
                    int team = i < team0 ? 0 : 1;
                    info.Players.Add(new MatchAssignment { PlayerId = shuffled[i].Session.PlayerId, Side = team, Team = team });
                }
                break;
            }
            case Protocol.ModeMirror:
            {
                int forPlayer = players.Count(p => p.Vote == Protocol.SidePlayer);
                int forEnemy = players.Count(p => p.Vote == Protocol.SideEnemy);
                int side = forPlayer > forEnemy ? Protocol.SidePlayer
                    : forEnemy > forPlayer ? Protocol.SideEnemy
                    : RandomInt(2);
                info.MirrorSide = side;
                foreach (var p in players)
                    info.Players.Add(new MatchAssignment { PlayerId = p.Session.PlayerId, Side = side });
                break;
            }
            default: // versus
                for (int i = 0; i < shuffled.Count; i++)
                    info.Players.Add(new MatchAssignment { PlayerId = shuffled[i].Session.PlayerId, Side = i == 0 ? 0 : 1 });
                break;
        }

        var match = new MatchState(info, Now);
        foreach (var a in info.Players) match.Participants[a.PlayerId] = new Participant(a);
        foreach (var p in players)
        {
            match.Names[p.Session.PlayerId] = p.Session.Username;
            match.Hues[p.Session.PlayerId] = p.Session.ColorHue;
        }
        room.Match = match;
        room.Phase = Protocol.PhaseLoading;
        room.Song = null;

        AddSystemChatLocked(room, DescribeMatch(room, info), fx);
        foreach (var m in room.Members)
            fx.Send(m.Session.ConnectionId, Protocol.MatchLoading, info);
    }

    private string DescribeMatch(Room room, MatchInfo info)
    {
        string NameOf(string id) => room.Members.FirstOrDefault(m => m.Session.PlayerId == id)?.Session.Username ?? "?";
        string SideName(int s) => s == Protocol.SidePlayer ? "Player" : "Enemy";
        switch (info.Mode)
        {
            case Protocol.ModeTeams:
                var t0 = info.Players.Where(p => p.Team == 0).Select(p => NameOf(p.PlayerId));
                var t1 = info.Players.Where(p => p.Team == 1).Select(p => NameOf(p.PlayerId));
                return $"Round starting: {info.Song.Name}. Team Player: {string.Join(", ", t0)} | Team Enemy: {string.Join(", ", t1)}.";
            case Protocol.ModeMirror:
                return $"Round starting: {info.Song.Name}. Everybody plays as {SideName(info.MirrorSide)}.";
            default:
                return $"Round starting: {info.Song.Name}. " +
                       string.Join(" vs ", info.Players.Select(p => $"{NameOf(p.PlayerId)} ({SideName(p.Side)})")) + ".";
        }
    }

    /// <summary>All players loaded: send the synced start time. False when there aren't enough players.</summary>
    private bool TryStartMatchLocked(Room room, Effects fx)
    {
        var match = room.Match;
        if (match == null || room.Phase != Protocol.PhaseLoading) return false;
        if (match.Participants.Count < MinParticipants(match.Info.Mode)) return false;
        if (match.Info.Mode == Protocol.ModeVersus && match.Participants.Count != 2) return false;
        if (!match.Participants.Keys.All(match.Loaded.Contains)) return true; // still waiting, but valid

        double maxPing = room.Members
            .Where(m => match.Participants.ContainsKey(m.Session.PlayerId))
            .Select(m => m.Session.AvgPing).DefaultIfEmpty(0).Max();
        int buffer = (int)Math.Clamp(maxPing * 2 + 400, 600, 2500);
        match.Info.StartTimestamp = NowMs + buffer + CountdownMs;
        // Drop assignments of players who were removed while loading.
        match.Info.Players.RemoveAll(a => !match.Participants.ContainsKey(a.PlayerId));
        room.Phase = Protocol.PhasePlaying;

        var packet = new MatchStartPacket { MatchId = match.Info.MatchId, StartTimestamp = match.Info.StartTimestamp };
        foreach (var m in room.Members)
            fx.Send(m.Session.ConnectionId, Protocol.MatchStart, packet);
        BroadcastRoomLocked(room, fx);
        return true;
    }

    private void CancelMatchLocked(Room room, Effects fx, string reason)
    {
        var match = room.Match;
        room.Match = null;
        room.Phase = Protocol.PhaseSongSelected;
        room.Song = match?.Info.Song;
        if (room.Song == null) room.Phase = Protocol.PhaseLobby;
        ResetReadyLocked(room, keepPicker: false);
        foreach (var m in room.Members) UpdateMemberLevelStateLocked(m);
        AddSystemChatLocked(room, reason, fx);
        EnsureTurnLocked(room);
        BroadcastRoomLocked(room, fx);
    }

    private void FinishRoundLocked(Room room, Effects fx)
    {
        var match = room.Match;
        if (match == null) return;

        var results = new List<PlayerResult>();
        foreach (var p in match.Participants.Values)
        {
            var member = room.Members.FirstOrDefault(m => m.Session.PlayerId == p.Assignment.PlayerId);
            var name = member?.Session.Username ?? match.Names.GetValueOrDefault(p.Assignment.PlayerId, "Player");
            int hue = member?.Session.ColorHue ?? match.Hues.GetValueOrDefault(p.Assignment.PlayerId, 180);
            var r = new PlayerResult
            {
                PlayerId = p.Assignment.PlayerId, Username = name, ColorHue = hue,
                Side = p.Assignment.Side, Team = p.Assignment.Team,
                Disconnected = p.Gone && (p.Final == null || p.Final.Quit),
            };
            if (p.Final != null)
            {
                r.Score = p.Final.Score; r.Accuracy = Math.Clamp(p.Final.Accuracy, 0f, 100f); r.Misses = Math.Max(0, p.Final.Misses);
                r.Sicks = p.Final.Sicks; r.Goods = p.Final.Goods; r.Bads = p.Final.Bads; r.Shits = p.Final.Shits;
                r.MaxCombo = p.Final.MaxCombo;
            }
            else if (p.Last != null)
            {
                r.Score = p.Last.Score; r.Accuracy = Math.Clamp(p.Last.Accuracy, 0f, 100f); r.Misses = p.Last.Misses;
                r.Sicks = p.Last.Sicks; r.Goods = p.Last.Goods; r.Bads = p.Last.Bads; r.Shits = p.Last.Shits;
                r.MaxCombo = p.Last.MaxCombo;
            }
            results.Add(r);
        }

        var round = new RoundResult
        {
            MatchId = match.Info.MatchId, Mode = match.Info.Mode, Song = match.Info.Song, FinishedAt = NowMs,
        };
        RankResults(results, round);
        round.Players = results;

        room.History.Add(round);
        if (room.History.Count > MaxRoundHistory) room.History.RemoveAt(0);
        room.HistoryVersion++;
        if (results.Count(r => !r.Disconnected) >= 2) fx.RoundsToRecord.Add(round);

        foreach (var m in room.Members)
            fx.Send(m.Session.ConnectionId, Protocol.MatchResults, round);
        AddSystemChatLocked(room, DescribeResult(round), fx);

        // Next round: the turn moves on from whoever picked this song.
        foreach (var t in _transfers.Values.Where(t => t.Room == room).ToList()) EndTransferLocked(t, fx);
        room.Match = null;
        room.Song = null;
        room.Phase = Protocol.PhaseLobby;
        ResetReadyLocked(room, keepPicker: false);
        foreach (var m in room.Members) UpdateMemberLevelStateLocked(m);
        var picker = room.Members.FirstOrDefault(m => m.Session.PlayerId == match.Info.Song.PickedBy);
        if (picker != null) room.TurnSeq = picker.JoinSeq;
        AdvanceTurnLocked(room);
        AnnounceTurnLocked(room, fx);
        BroadcastRoomLocked(room, fx);
    }

    /// <summary>
    /// Orders <paramref name="results"/> and sets places. Disconnected players are always last.
    /// Teams: the team with the higher average score wins; places are individual ranks.
    /// </summary>
    internal static void RankResults(List<PlayerResult> results, RoundResult round)
    {
        results.Sort((a, b) =>
        {
            int d = a.Disconnected.CompareTo(b.Disconnected);
            if (d != 0) return d;
            int s = b.Score.CompareTo(a.Score);
            return s != 0 ? s : b.Accuracy.CompareTo(a.Accuracy);
        });
        for (int i = 0; i < results.Count; i++)
        {
            bool tie = i > 0 && results[i].Score == results[i - 1].Score && results[i].Disconnected == results[i - 1].Disconnected;
            results[i].Place = tie ? results[i - 1].Place : i + 1;
        }

        if (round.Mode == Protocol.ModeTeams)
        {
            double Avg(int team)
            {
                var list = results.Where(r => r.Team == team).ToList();
                return list.Count == 0 ? 0 : list.Average(r => (double)r.Score);
            }
            double a0 = Avg(0), a1 = Avg(1);
            round.TeamScores = new List<double> { Math.Round(a0, 1), Math.Round(a1, 1) };
            round.WinningTeam = a0 > a1 ? 0 : a1 > a0 ? 1 : -1;
        }
    }

    private static string DescribeResult(RoundResult round)
    {
        if (round.Mode == Protocol.ModeTeams)
        {
            return round.WinningTeam switch
            {
                0 => $"Team Player wins {round.Song.Name}! ({round.TeamScores[0]:0} vs {round.TeamScores[1]:0} avg)",
                1 => $"Team Enemy wins {round.Song.Name}! ({round.TeamScores[1]:0} vs {round.TeamScores[0]:0} avg)",
                _ => $"{round.Song.Name} ended in a draw between the teams.",
            };
        }
        var winners = round.Players.Where(p => p.Place == 1 && !p.Disconnected).ToList();
        if (winners.Count == 0) return $"{round.Song.Name} ended.";
        if (winners.Count > 1) return $"{round.Song.Name} ended in a draw.";
        return $"{winners[0].Username} wins {round.Song.Name} with {winners[0].Score:N0} points!";
    }

    private void ResetReadyLocked(Room room, bool keepPicker)
    {
        foreach (var m in room.Members)
        {
            bool picker = keepPicker && room.Song != null && m.Session.PlayerId == room.Song.PickedBy;
            m.Ready = picker && !m.Spectate;
            m.Vote = -1;
        }
    }

    private void ClearSongLocked(Room room, Effects fx)
    {
        foreach (var t in _transfers.Values.Where(t => t.Room == room).ToList()) EndTransferLocked(t, fx);
        room.Song = null;
        if (room.Phase == Protocol.PhaseSongSelected) room.Phase = Protocol.PhaseLobby;
        ResetReadyLocked(room, keepPicker: false);
        foreach (var m in room.Members)
        {
            m.LevelState = Protocol.LevelMissing;
            m.LevelProgress = 0f;
            m.UploadProgress = -1f;
        }
    }

    private List<Member> Shuffle(List<Member> list)
    {
        var copy = list.ToList();
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = RandomInt(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: level transfers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The picked local level: one holder uploads it once (unless cached), everybody else downloads it.</summary>
    private void StartTransferLocked(Room room, Effects fx, string? excludeUploader = null)
    {
        var song = room.Song ?? room.Match?.Info.Song;
        if (song == null || song.Source != Protocol.SourceLocal) return;
        if (room.Members.All(m => m.Levels.ContainsKey(song.Hash))) return;

        if (room.Cache.TryGetValue(song.Hash, out var cached) && File.Exists(cached.Path))
        {
            var t = NewTransfer(room, song, uploaderId: "");
            t.FilePath = cached.Path;
            cached.ExpiresAt = Now.AddMinutes(_options.CacheMinutes);
            foreach (var m in room.Members) OfferDownloadLocked(room, m, fx);
            return;
        }

        // Uploader: the picker, then the host, then anybody who has it.
        var holders = room.Members.Where(m => m.Levels.ContainsKey(song.Hash) && m.Session.PlayerId != excludeUploader).ToList();
        var uploader = holders.FirstOrDefault(m => m.Session.PlayerId == song.PickedBy)
                       ?? holders.FirstOrDefault(m => m == room.Host)
                       ?? holders.FirstOrDefault();
        if (uploader == null)
        {
            foreach (var m in room.Members.Where(m => !m.Levels.ContainsKey(song.Hash)))
            {
                m.LevelState = Protocol.LevelFailed;
                fx.Send(m.Session.ConnectionId, Protocol.TransferFailed, new TransferFailedPacket
                {
                    Hash = song.Hash, Reason = "Nobody in the room can send this level.",
                });
            }
            return;
        }

        var tr = NewTransfer(room, song, uploader.Session.PlayerId);
        uploader.UploadProgress = 0f;
        fx.Send(uploader.Session.ConnectionId, Protocol.TransferUpload, new TransferUploadPacket
        {
            TransferId = tr.Id, Token = tr.UploadToken, Hash = song.Hash, FileName = song.FileName, Size = song.Size,
        });
    }

    private Transfer NewTransfer(Room room, SongInfo song, string uploaderId)
    {
        var t = new Transfer(NewId(16), room, song.Hash, song.Name, song.FileName, song.Size, uploaderId, NewId(24))
        {
            LastActivity = Now,
        };
        _transfers[t.Id] = t;
        return t;
    }

    /// <summary>Gives <paramref name="m"/> a download ticket for the room's current level if they lack it and it's on the server.</summary>
    private void OfferDownloadLocked(Room room, Member m, Effects fx)
    {
        var song = room.Song ?? room.Match?.Info.Song;
        if (song == null || song.Source != Protocol.SourceLocal || m.Levels.ContainsKey(song.Hash)) return;

        var t = _transfers.Values.FirstOrDefault(x => x.Room == room && x.Hash == song.Hash);
        if (t == null)
        {
            // Nothing running (e.g. a mid-match joiner after the transfer finished): start one.
            StartTransferLocked(room, fx);
            t = _transfers.Values.FirstOrDefault(x => x.Room == room && x.Hash == song.Hash);
            if (t == null) return;
        }
        if (t.FilePath == null) return; // still uploading; everybody gets a ticket when it's done
        if (t.Downloads.ContainsKey(m.Session.PlayerId)) return;

        var d = new Download(m.Session.PlayerId, NewId(24)) { LastActivity = Now };
        t.Downloads[d.PlayerId] = d;
        m.LevelState = Protocol.LevelDownloading;
        m.LevelProgress = 0f;
        fx.Send(m.Session.ConnectionId, Protocol.TransferDownload, new TransferDownloadPacket
        {
            TransferId = t.Id, Token = d.Token, Hash = t.Hash, Name = t.Name, FileName = t.FileName, Size = t.Size,
        });
    }

    private void FailTransferLocked(Transfer t, Effects fx, string reason, bool tryNextHolder)
    {
        string failedUploader = t.UploaderId;
        EndTransferLocked(t, fx);
        var room = t.Room;
        foreach (var m in room.Members) m.UploadProgress = -1f;
        if (!_rooms.ContainsKey(room.Id)) return;

        var song = room.Song ?? room.Match?.Info.Song;
        if (tryNextHolder && song != null && song.Hash == t.Hash
            && room.Members.Any(m => m.Levels.ContainsKey(t.Hash) && m.Session.PlayerId != failedUploader))
        {
            StartTransferLocked(room, fx, excludeUploader: failedUploader);
        }
        else
        {
            foreach (var m in room.Members.Where(m => !m.Levels.ContainsKey(t.Hash)))
            {
                m.LevelState = Protocol.LevelFailed;
                fx.Send(m.Session.ConnectionId, Protocol.TransferFailed, new TransferFailedPacket { Hash = t.Hash, Reason = reason });
            }
            AddSystemChatLocked(room, $"Couldn't send {t.Name}: {reason}", fx);
        }
        BroadcastRoomLocked(room, fx);
    }

    private void EndTransferLocked(Transfer t, Effects fx)
    {
        _transfers.Remove(t.Id);
        fx.ToCancel.Add(t.Cts);
        foreach (var d in t.Downloads.Values) fx.ToCancel.Add(d.Cts);
        t.Downloads.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: broadcasting
    // ══════════════════════════════════════════════════════════════════════

    private bool ProgressBroadcastDue(Room room)
    {
        var now = Now;
        if ((now - room.LastProgressBroadcast).TotalMilliseconds < _options.ProgressIntervalMs) return false;
        room.LastProgressBroadcast = now;
        return true;
    }

    private void AddSystemChatLocked(Room room, string text, Effects fx) =>
        AddChatLocked(room, new ChatMessage { System = true, Text = text, Timestamp = NowMs }, fx);

    private void AddChatLocked(Room room, ChatMessage msg, Effects fx)
    {
        room.Chat.Add(msg);
        if (room.Chat.Count > MaxChatHistory) room.Chat.RemoveAt(0);
        foreach (var m in room.Members)
            fx.Send(m.Session.ConnectionId, Protocol.Chat, msg);
    }

    private static void SendChatHistoryLocked(Room room, Session s, Effects fx)
    {
        foreach (var msg in room.Chat)
            fx.Send(s.ConnectionId, Protocol.Chat, msg);
    }

    /// <summary>Sends every member their own view of the room.</summary>
    private void BroadcastRoomLocked(Room room, Effects fx)
    {
        if (!_rooms.ContainsKey(room.Id)) return;
        room.LastProgressBroadcast = Now;
        string? blocker = StartBlockerLocked(room);
        var turn = room.TurnMember;
        bool inMatch = room.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying;

        var members = room.Members.Select(m =>
        {
            string id = m.Session.PlayerId;
            string status = inMatch
                ? (room.Match!.Participants.ContainsKey(id) ? Protocol.StatusPlaying : Protocol.StatusSpectating)
                : m.Spectate ? Protocol.StatusSpectating
                : m.Ready ? Protocol.StatusReady : Protocol.StatusNotReady;
            bool uploading = m.UploadProgress >= 0f;
            return new RoomMember
            {
                Id = id, Username = m.Session.Username, ColorHue = m.Session.ColorHue, IsHost = m == room.Host,
                Status = status, Spectate = m.Spectate,
                LevelStatus = m.LevelState, LevelProgress = uploading ? m.UploadProgress : m.LevelProgress, Vote = m.Vote,
            };
        }).ToList();

        // Union of every local level in the room.
        var all = new Dictionary<string, LevelEntry>(StringComparer.Ordinal);
        foreach (var m in room.Members)
            foreach (var (hash, entry) in m.Levels)
                all.TryAdd(hash, entry);

        foreach (var member in room.Members)
        {
            // The level list and history can be large: only sent when they changed for this member.
            List<RoomLevel>? levels = null;
            if (member.SentLevelsVersion != room.LevelsVersion)
            {
                member.SentLevelsVersion = room.LevelsVersion;
                levels = new List<RoomLevel>(all.Count);
            }
            List<RoundResult>? history = null;
            if (member.SentHistoryVersion != room.HistoryVersion)
            {
                member.SentHistoryVersion = room.HistoryVersion;
                history = room.History.ToList();
            }
            foreach (var (hash, entry) in levels == null ? new Dictionary<string, LevelEntry>() : all)
            {
                bool youHave = member.Levels.TryGetValue(hash, out var own);
                var src = own ?? entry;
                levels!.Add(new RoomLevel
                {
                    Hash = hash, Name = src.Name, FileName = src.FileName, Size = src.Size, Stars = src.Stars,
                    YouHave = youHave,
                    Holders = room.Members.Where(o => o != member && o.Levels.ContainsKey(hash)).Select(o => o.Session.PlayerId).ToList(),
                    Pickable = IsPickableLocked(room, hash),
                });
            }

            var settings = new RoomSettings
            {
                Mode = room.Settings.Mode,
                SongSelection = room.Settings.SongSelection,
                MaxPlayers = room.Settings.MaxPlayers,
                AllowGuestLocalLevels = room.Settings.AllowGuestLocalLevels,
                Password = member == room.Host ? room.Settings.Password : "",
            };

            fx.Send(member.Session.ConnectionId, Protocol.RoomState, new RoomStatePacket
            {
                RoomId = room.Id,
                Name = room.Name,
                Settings = settings,
                HasPassword = room.Settings.Password.Length > 0,
                Phase = room.Phase,
                Members = members,
                HostId = room.Host?.Session.PlayerId ?? "",
                YouId = member.Session.PlayerId,
                TurnPlayerId = !inMatch && turn != null && CanPick(turn) ? turn.Session.PlayerId : "",
                Song = room.Song,
                Match = room.Match?.Info,
                StartBlocker = blocker ?? "",
                Levels = levels,
                History = history,
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals: validation
    // ══════════════════════════════════════════════════════════════════════

    private static RoomSettings? SanitizeSettings(RoomSettings? s, int minPlayers, out string? error)
    {
        error = null;
        s ??= new RoomSettings();
        string mode = s.Mode is Protocol.ModeVersus or Protocol.ModeTeams or Protocol.ModeMirror ? s.Mode : "";
        if (mode.Length == 0) { error = "Unknown game mode."; return null; }
        string sel = s.SongSelection is Protocol.SelectTurns or Protocol.SelectHost ? s.SongSelection : "";
        if (sel.Length == 0) { error = "Unknown song selection mode."; return null; }
        if (s.MaxPlayers < Protocol.MinPlayers || s.MaxPlayers > Protocol.MaxPlayers)
        { error = $"The player limit must be between {Protocol.MinPlayers} and {Protocol.MaxPlayers}."; return null; }
        if (s.MaxPlayers < minPlayers)
        { error = $"There are already {minPlayers} players in the room."; return null; }
        string password = (s.Password ?? "").Trim();
        if (password.Length > 32) { error = "The password can be at most 32 characters."; return null; }
        return new RoomSettings
        {
            Mode = mode, SongSelection = sel, Password = password, MaxPlayers = s.MaxPlayers,
            AllowGuestLocalLevels = s.AllowGuestLocalLevels,
        };
    }

    internal static string ModeName(string mode) => mode switch
    {
        Protocol.ModeTeams => "Teams",
        Protocol.ModeMirror => "Mirror",
        _ => "Versus",
    };

    internal static string SelectionName(string sel) => sel == Protocol.SelectHost ? "host only" : "turns";

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
            float stars = float.IsFinite(l.Stars) ? Math.Clamp(l.Stars, 0f, 99f) : 0f;
            result.TryAdd(hash, new LevelEntry { Hash = hash, Name = name, FileName = fileName, Size = l.Size, Stars = stars });
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
        string s = Clean(value, 32);
        return s.Length > 0 ? s : Clean(fallback, 100);
    }

    internal static string Clean(string? value, int max)
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

    private static bool SecretEquals(string expected, string? actual) =>
        actual != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual.Trim()));

    private static string NewId(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private string NewRoomId()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string id;
        do id = RandomNumberGenerator.GetString(chars, 6);
        while (_rooms.ContainsKey(id));
        return id;
    }

    // ══════════════════════════════════════════════════════════════════════
    // State types
    // ══════════════════════════════════════════════════════════════════════

    private sealed class Session(string connectionId)
    {
        public string ConnectionId { get; } = connectionId;
        public string PlayerId { get; set; } = "";
        public string Username { get; set; } = "Player";
        public int ColorHue { get; set; } = 180;
        public bool Greeted { get; set; }
        public double AvgPing { get; set; }
        public Member? Member { get; set; }
    }

    private sealed class Room(string id, string name, RoomSettings settings, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public RoomSettings Settings { get; set; } = settings;
        /// <summary>Join order. The first member is always the host.</summary>
        public List<Member> Members { get; } = new();
        public Member? Host => Members.Count > 0 ? Members[0] : null;
        public string Phase { get; set; } = Protocol.PhaseLobby;
        /// <summary>JoinSeq of the member whose turn it is.</summary>
        public long TurnSeq { get; set; }
        public Member? TurnMember => Members.FirstOrDefault(m => m.JoinSeq == TurnSeq);
        public SongInfo? Song { get; set; }
        public MatchState? Match { get; set; }
        public List<RoundResult> History { get; } = new();
        public List<ChatMessage> Chat { get; } = new();
        public Dictionary<string, DateTimeOffset> Bans { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CachedFile> Cache { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastProgressBroadcast { get; set; }
        /// <summary>Bumped whenever the room's level list or pickability changes.</summary>
        public int LevelsVersion { get; set; } = 1;
        public int HistoryVersion { get; set; } = 1;
    }

    private sealed class Member(Session session, Room room, long joinSeq)
    {
        public Session Session { get; set; } = session;
        public Room Room { get; } = room;
        public long JoinSeq { get; } = joinSeq;
        public Dictionary<string, LevelEntry> Levels { get; set; } = new();
        public bool Ready { get; set; }
        public bool Spectate { get; set; }
        public int Vote { get; set; } = -1;
        public string LevelState { get; set; } = Protocol.LevelMissing;
        public float LevelProgress { get; set; }
        /// <summary>0-1 while uploading the room's level, otherwise -1.</summary>
        public float UploadProgress { get; set; } = -1f;
        public DateTimeOffset LastChat { get; set; }
        public int SentLevelsVersion { get; set; }
        public int SentHistoryVersion { get; set; }
    }

    private sealed class MatchState(MatchInfo info, DateTimeOffset createdAt)
    {
        public MatchInfo Info { get; } = info;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public Dictionary<string, Participant> Participants { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Loaded { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? FirstFinishAt { get; set; }
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Hues { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Participant(MatchAssignment assignment)
    {
        public MatchAssignment Assignment { get; } = assignment;
        public FinishMatchRequest? Final { get; set; }
        public PlayerStatePacket? Last { get; set; }
        /// <summary>Left the room during the match.</summary>
        public bool Gone { get; set; }
    }

    private sealed class CachedFile(string path, long size)
    {
        public string Path { get; } = path;
        public long Size { get; } = size;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class Transfer(string id, Room room, string hash, string name, string fileName, long size,
        string uploaderId, string uploadToken)
    {
        public string Id { get; } = id;
        public Room Room { get; } = room;
        public string Hash { get; } = hash;
        public string Name { get; } = name;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public string UploaderId { get; } = uploaderId;
        public string UploadToken { get; } = uploadToken;
        public CancellationTokenSource Cts { get; } = new();
        public bool UploadStarted { get; set; }
        public long UploadBytes { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        /// <summary>Set once the file is on the server (uploaded or from the room cache).</summary>
        public string? FilePath { get; set; }
        public Dictionary<string, Download> Downloads { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Download(string playerId, string token)
    {
        public string PlayerId { get; } = playerId;
        public string Token { get; } = token;
        public CancellationTokenSource Cts { get; } = new();
        public long Bytes { get; set; }
        public DateTimeOffset LastActivity { get; set; }
    }
}
