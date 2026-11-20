using System.Text.Json;
using System.Threading.Channels;
using FunkyMakerServer.Models;
using FunkyMakerTransferServer;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FunkyMakerServer.Services;

/// <summary>
/// Service for managing global leaderboard and match history.
/// Uses in-memory cache backed by MongoDB (when MONGODB_URI is set) and local JSON files.
/// </summary>
public class LeaderboardService : IHostedService
{
    private const int MaxStoredMatches = 1000;
    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };

    private static readonly string DataFolder = ResolveDataFolder();
    private static readonly string LeaderboardFile = Path.Combine(DataFolder, "global_leaderboard.json");
    private static readonly string MatchHistoryFile = Path.Combine(DataFolder, "match_history.json");

    private readonly object _lock = new();

    private Dictionary<string, LeaderboardEntryPacket> _leaderboard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UserDirectoryEntry> _connectedProfiles = new(StringComparer.Ordinal);
    private List<MatchHistoryPacket> _matchHistory = new();
    private readonly HashSet<string> _recordedMatchIds = new(StringComparer.Ordinal);

    private IMongoCollection<LeaderboardEntryPacket>? _leaderboardCollection;
    private IMongoCollection<MatchHistoryPacket>? _matchHistoryCollection;
    private Channel<Func<Task>>? _mongoWriteQueue;
    private Task? _mongoWorkerTask;
    private string? _mongoDbName;
    private string? _lastMongoError;

    public bool IsMongoDbEnabled => _leaderboardCollection != null && _matchHistoryCollection != null;
    public string? DatabaseName => _mongoDbName;
    public string? LastMongoError => _lastMongoError;

    static LeaderboardService()
    {
        RegisterBsonClassMaps();
    }

    private static void RegisterBsonClassMaps()
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(LeaderboardEntryPacket)))
        {
            BsonClassMap.RegisterClassMap<LeaderboardEntryPacket>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
            });
        }

        if (!BsonClassMap.IsClassMapRegistered(typeof(MatchHistoryPacket)))
        {
            BsonClassMap.RegisterClassMap<MatchHistoryPacket>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
            });
        }

        if (!BsonClassMap.IsClassMapRegistered(typeof(PlayerMatchData)))
        {
            BsonClassMap.RegisterClassMap<PlayerMatchData>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
            });
        }
    }

    /// <summary>
    /// FUNKYMAKER_DATA_DIR, else leaderboard_data next to the binary. If that folder can't be
    /// created / written (e.g. the Docker image runs as a non-root user and /app is read-only),
    /// falls back to a folder in the system temp directory instead of crashing at startup.
    /// </summary>
    private static string ResolveDataFolder()
    {
        string? envDir = Environment.GetEnvironmentVariable("FUNKYMAKER_DATA_DIR");
        string preferred = !string.IsNullOrWhiteSpace(envDir)
            ? envDir.Trim()
            : Path.Combine(AppContext.BaseDirectory, "leaderboard_data");
        if (IsWritable(preferred)) return preferred;

        string fallback = Path.Combine(Path.GetTempPath(), "funkymaker_leaderboard_data");
        Console.WriteLine($"[Leaderboard] Can't write to {preferred}; using {fallback} instead");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public LeaderboardService()
    {
        // Ensure data folder exists for local JSON fallback/backup
        Directory.CreateDirectory(DataFolder);
        Console.WriteLine($"[Leaderboard] Data folder: {DataFolder}");

        ConfigureMongoDb();
        LoadData();
    }

    private void ConfigureMongoDb()
    {
        string? mongoUri = Environment.GetEnvironmentVariable("MONGODB_URI");
        if (string.IsNullOrWhiteSpace(mongoUri))
            mongoUri = Environment.GetEnvironmentVariable("MONGO_URI");

        if (string.IsNullOrWhiteSpace(mongoUri))
        {
            Console.WriteLine("[Leaderboard] MONGODB_URI not set — using local JSON persistence");
            return;
        }

        string dbName = Environment.GetEnvironmentVariable("MONGODB_DB") ?? "funkymaker";
        if (string.IsNullOrWhiteSpace(dbName))
            dbName = "funkymaker";

        _mongoDbName = dbName.Trim();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(mongoUri.Trim());
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            settings.ConnectTimeout = TimeSpan.FromSeconds(15);

            var client = new MongoClient(settings);
            var database = client.GetDatabase(_mongoDbName);

            _leaderboardCollection = database.GetCollection<LeaderboardEntryPacket>("global_leaderboard");
            _matchHistoryCollection = database.GetCollection<MatchHistoryPacket>("match_history");

            EnsureMongoIndexes();
            StartMongoWriteWorker();
            _lastMongoError = null;

            Console.WriteLine($"[Leaderboard] MongoDB persistence enabled (db: {_mongoDbName})");
        }
        catch (Exception ex)
        {
            _lastMongoError = ex.Message;
            Console.WriteLine($"[Leaderboard] ERROR initializing MongoDB ({ex.Message}) — falling back to local JSON");
            _leaderboardCollection = null;
            _matchHistoryCollection = null;
            _mongoWriteQueue = null;
        }
    }

    private void EnsureMongoIndexes()
    {
        if (_leaderboardCollection == null || _matchHistoryCollection == null)
            return;

        try
        {
            _leaderboardCollection.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<LeaderboardEntryPacket>(
                    Builders<LeaderboardEntryPacket>.IndexKeys.Ascending(e => e.PlayerId),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<LeaderboardEntryPacket>(
                    Builders<LeaderboardEntryPacket>.IndexKeys
                        .Descending(e => e.EloRating)
                        .Descending(e => e.TotalScore))
            });

            _matchHistoryCollection.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<MatchHistoryPacket>(
                    Builders<MatchHistoryPacket>.IndexKeys.Ascending(m => m.MatchId),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<MatchHistoryPacket>(
                    Builders<MatchHistoryPacket>.IndexKeys.Descending(m => m.Timestamp))
            });
        }
        catch (Exception ex)
        {
            _lastMongoError = ex.Message;
            Console.WriteLine($"[Leaderboard] WARNING creating MongoDB indexes: {ex.Message}");
        }
    }

    private void StartMongoWriteWorker()
    {
        _mongoWriteQueue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _mongoWorkerTask = Task.Run(ProcessMongoQueueAsync);
    }

    private async Task ProcessMongoQueueAsync()
    {
        if (_mongoWriteQueue == null)
            return;

        await foreach (var workItem in _mongoWriteQueue.Reader.ReadAllAsync())
        {
            try
            {
                await workItem();
                _lastMongoError = null;
            }
            catch (Exception ex)
            {
                _lastMongoError = ex.Message;
                Console.WriteLine($"[Leaderboard] ERROR persisting to MongoDB: {ex.Message}");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mongoWriteQueue == null)
            return;

        Console.WriteLine("[Leaderboard] Flushing pending MongoDB writes before shutdown...");
        _mongoWriteQueue.Writer.TryComplete();

        if (_mongoWorkerTask != null)
        {
            var timeoutTask = cancellationToken.CanBeCanceled
                ? Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)
                : Task.Delay(TimeSpan.FromSeconds(10));

            var completed = await Task.WhenAny(_mongoWorkerTask, timeoutTask);
            if (completed != _mongoWorkerTask)
            {
                Console.WriteLine("[Leaderboard] WARNING: Timed out waiting for MongoDB write queue to drain");
            }
            else
            {
                Console.WriteLine("[Leaderboard] MongoDB write queue flushed cleanly");
            }
        }
    }

    /// <summary>
    /// Load leaderboard and match history from MongoDB (when configured) or local disk.
    /// </summary>
    private void LoadData()
    {
        lock (_lock)
        {
            bool loadedFromMongo = TryLoadFromMongoLocked();

            if (!loadedFromMongo)
            {
                LoadFromDiskLocked();
            }
            else if (_leaderboard.Count == 0 && _matchHistory.Count == 0)
            {
                // One-time auto-migration if MongoDB starts empty but local JSON already has entries
                LoadFromDiskLocked();
                if (_leaderboard.Count > 0 || _matchHistory.Count > 0)
                {
                    SeedMongoFromMemoryLocked();
                }
            }
        }
    }

    private bool TryLoadFromMongoLocked()
    {
        if (_leaderboardCollection == null || _matchHistoryCollection == null)
            return false;

        try
        {
            var players = _leaderboardCollection
                .Find(Builders<LeaderboardEntryPacket>.Filter.Empty)
                .ToList();

            var matches = _matchHistoryCollection
                .Find(Builders<MatchHistoryPacket>.Filter.Empty)
                .SortByDescending(m => m.Timestamp)
                .Limit(MaxStoredMatches)
                .ToList();

            _leaderboard = new Dictionary<string, LeaderboardEntryPacket>(StringComparer.Ordinal);
            foreach (var entry in players)
            {
                if (!string.IsNullOrWhiteSpace(entry.PlayerId))
                    _leaderboard[entry.PlayerId] = entry;
            }

            _matchHistory = matches;
            _recordedMatchIds.Clear();
            foreach (var match in _matchHistory)
            {
                if (!string.IsNullOrWhiteSpace(match.MatchId))
                    _recordedMatchIds.Add(match.MatchId);
            }

            Console.WriteLine($"[Leaderboard] Loaded {_leaderboard.Count} players and {_matchHistory.Count} matches from MongoDB");
            _lastMongoError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastMongoError = ex.Message;
            Console.WriteLine($"[Leaderboard] WARNING loading from MongoDB ({ex.Message}) — falling back to disk cache");
            return false;
        }
    }

    private void LoadFromDiskLocked()
    {
        if (File.Exists(LeaderboardFile))
        {
            try
            {
                string json = File.ReadAllText(LeaderboardFile);
                var entries = JsonSerializer.Deserialize<List<LeaderboardEntryPacket>>(json) ?? new();
                _leaderboard = new Dictionary<string, LeaderboardEntryPacket>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.PlayerId))
                        _leaderboard[entry.PlayerId] = entry;
                }
                Console.WriteLine($"[Leaderboard] Loaded {_leaderboard.Count} players from disk");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Leaderboard] ERROR loading leaderboard: {ex.Message}");
                _leaderboard = new(StringComparer.Ordinal);
            }
        }

        if (File.Exists(MatchHistoryFile))
        {
            try
            {
                string json = File.ReadAllText(MatchHistoryFile);
                _matchHistory = (JsonSerializer.Deserialize<List<MatchHistoryPacket>>(json) ?? new())
                    .OrderByDescending(m => m.Timestamp)
                    .Take(MaxStoredMatches)
                    .ToList();
                _recordedMatchIds.Clear();
                foreach (var match in _matchHistory)
                {
                    if (!string.IsNullOrWhiteSpace(match.MatchId))
                        _recordedMatchIds.Add(match.MatchId);
                }
                Console.WriteLine($"[Leaderboard] Loaded {_matchHistory.Count} matches from disk");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Leaderboard] ERROR loading match history: {ex.Message}");
                _matchHistory = new();
                _recordedMatchIds.Clear();
            }
        }
    }

    private void SeedMongoFromMemoryLocked()
    {
        if (_mongoWriteQueue == null || _leaderboardCollection == null || _matchHistoryCollection == null)
            return;

        var playersSnapshot = _leaderboard.Values.Select(ClonePlayer).ToList();
        var matchesSnapshot = _matchHistory.Select(CloneMatch).ToList();
        Console.WriteLine($"[Leaderboard] Migrating {playersSnapshot.Count} players and {matchesSnapshot.Count} matches from disk to MongoDB");

        _mongoWriteQueue.Writer.TryWrite(async () =>
        {
            foreach (var player in playersSnapshot)
            {
                await UpsertPlayerToMongoAsync(player);
            }

            foreach (var match in matchesSnapshot)
            {
                await InsertMatchToMongoAsync(match);
            }
        });
    }

    /// <summary>
    /// Save leaderboard and match history to disk atomically.
    /// </summary>
    private void SaveData()
    {
        lock (_lock)
        {
            try
            {
                var entries = _leaderboard.Values.ToList();
                string leaderboardJson = JsonSerializer.Serialize(entries, WriteJsonOptions);
                WriteFileAtomic(LeaderboardFile, leaderboardJson);

                string historyJson = JsonSerializer.Serialize(_matchHistory, WriteJsonOptions);
                WriteFileAtomic(MatchHistoryFile, historyJson);

                Console.WriteLine($"[Leaderboard] Saved {entries.Count} players and {_matchHistory.Count} matches to disk");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Leaderboard] ERROR saving data: {ex.Message}");
            }
        }
    }

    private static void WriteFileAtomic(string targetPath, string content)
    {
        string tempPath = targetPath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, targetPath, overwrite: true);
    }
    
    /// <summary>
    /// Get top players from global leaderboard.
    /// </summary>
    public List<LeaderboardEntryPacket> GetTopPlayers(int skip = 0, int take = 100)
    {
        lock (_lock)
        {
            return _leaderboard.Values
                .OrderByDescending(e => e.EloRating)
                .ThenByDescending(e => e.TotalScore)
                .Skip(skip)
                .Take(take)
                .Select(ClonePlayer)
                .ToList();
        }
    }

    /// <summary>
    /// Get recent matches from history.
    /// </summary>
    public List<MatchHistoryPacket> GetRecentMatches(string? playerId = null, int skip = 0, int take = 50)
    {
        lock (_lock)
        {
            var query = _matchHistory.AsEnumerable();

            // Filter by player ID if specified
            if (!string.IsNullOrEmpty(playerId))
            {
                query = query.Where(m =>
                    m.Player1.PlayerId == playerId ||
                    m.Player2.PlayerId == playerId ||
                    m.Players.Any(p => p.PlayerId == playerId));
            }

            return query
                .OrderByDescending(m => m.Timestamp)
                .Skip(skip)
                .Take(take)
                .Select(CloneMatch)
                .ToList();
        }
    }

    /// <summary>
    /// Get player stats from leaderboard.
    /// </summary>
    public LeaderboardEntryPacket? GetPlayerStats(string playerId)
    {
        lock (_lock)
        {
            return _leaderboard.TryGetValue(playerId, out var player) ? ClonePlayer(player) : null;
        }
    }

    /// <summary>
    /// Update or create player in leaderboard.
    /// </summary>
    public void UpdatePlayer(LeaderboardEntryPacket player)
    {
        LeaderboardEntryPacket snapshot;
        lock (_lock)
        {
            snapshot = ClonePlayer(player);
            _leaderboard[snapshot.PlayerId] = snapshot;
            SaveData();
            Console.WriteLine($"[Leaderboard] Updated player: {snapshot.Username} (ELO: {snapshot.EloRating}, W/L: {snapshot.Wins}/{snapshot.Losses})");
        }

        EnqueueMongoPlayerUpsert(ClonePlayer(snapshot));
    }

    /// <summary>
    /// Record a match result and update player stats. Returns false if MatchId was already recorded.
    /// </summary>
    public bool RecordMatch(MatchHistoryPacket match)
    {
        MatchHistoryPacket matchSnapshot;
        LeaderboardEntryPacket p1Snapshot;
        LeaderboardEntryPacket p2Snapshot;

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(match.MatchId))
            {
                match.MatchId = Guid.NewGuid().ToString("N");
            }
            else if (!_recordedMatchIds.Add(match.MatchId))
            {
                Console.WriteLine($"[Leaderboard] Duplicate match ignored: {match.MatchId}");
                return false;
            }

            // Keep in-memory list sorted newest-first and bounded
            _matchHistory.Insert(0, match);
            if (_matchHistory.Count > MaxStoredMatches)
            {
                var dropped = _matchHistory[^1];
                _matchHistory.RemoveAt(_matchHistory.Count - 1);
                if (!string.IsNullOrWhiteSpace(dropped.MatchId))
                    _recordedMatchIds.Remove(dropped.MatchId);
            }

            // Update player 1 & player 2 stats
            p1Snapshot = UpdatePlayerFromMatch(match.Player1, match.Winner == 1, match.Winner == 0);
            p2Snapshot = UpdatePlayerFromMatch(match.Player2, match.Winner == 2, match.Winner == 0);
            matchSnapshot = CloneMatch(match);

            SaveData();

            Console.WriteLine($"[Leaderboard] Recorded match ({match.MatchId}): {match.Player1.Username} vs {match.Player2.Username} on {match.LevelName}");
        }

        EnqueueMongoMatchWrite(matchSnapshot, p1Snapshot, p2Snapshot);
        return true;
    }

    /// <summary>K factor for one full match; split between the pairwise comparisons.</summary>
    public const double EloK = 32;

    /// <summary>
    /// Records a finished room round (2-8 players) with pairwise ELO: every player is compared with every
    /// other player (Versus / Mirror: by place; Teams: by the team result, teammates are not compared), and
    /// the rating change is K * sum(actual - expected) / number of comparisons. Disconnected players are kept
    /// in the history but get no stats or rating change.
    /// </summary>
    public bool RecordRound(RoundResult round)
    {
        var finished = round.Players.Where(p => !p.Disconnected).ToList();
        if (finished.Count < 2) return false;

        var match = new MatchHistoryPacket
        {
            MatchId = string.IsNullOrWhiteSpace(round.MatchId) ? Guid.NewGuid().ToString("N") : round.MatchId,
            LevelName = round.Song.Name,
            Mode = round.Mode,
            WinningTeam = round.WinningTeam,
            Timestamp = round.FinishedAt > 0 ? round.FinishedAt / 1000 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Players = round.Players.Select(p => new PlayerMatchData
            {
                PlayerId = p.PlayerId, Username = p.Username, ColorHue = p.ColorHue, Score = p.Score,
                Accuracy = p.Accuracy, MaxCombo = p.MaxCombo, Misses = p.Misses, Disconnected = p.Disconnected,
                Place = p.Place, Team = p.Team, Side = p.Side,
            }).ToList(),
        };
        bool teams = round.Mode == Protocol.ModeTeams;

        var snapshots = new List<LeaderboardEntryPacket>();
        MatchHistoryPacket matchSnapshot;
        lock (_lock)
        {
            if (!_recordedMatchIds.Add(match.MatchId))
                return false;

            var live = match.Players.Where(p => !p.Disconnected).ToList();
            var entries = live.ToDictionary(p => p, GetOrCreatePlayerLocked);
            var ratingBefore = entries.ToDictionary(kv => kv.Key, kv => (double)kv.Value.EloRating);

            foreach (var p in live)
            {
                double sum = 0;
                int comparisons = 0;
                foreach (var o in live)
                {
                    if (ReferenceEquals(o, p)) continue;
                    double actual;
                    if (teams)
                    {
                        if (o.Team == p.Team) continue;
                        actual = round.WinningTeam < 0 ? 0.5 : round.WinningTeam == p.Team ? 1 : 0;
                    }
                    else
                    {
                        actual = p.Place < o.Place ? 1 : p.Place > o.Place ? 0 : 0.5;
                    }
                    double expected = 1.0 / (1.0 + Math.Pow(10, (ratingBefore[o] - ratingBefore[p]) / 400.0));
                    sum += actual - expected;
                    comparisons++;
                }
                if (comparisons == 0) continue;
                p.EloChange = (int)Math.Round(EloK * sum / comparisons);
            }

            int firstPlaces = live.Count(x => x.Place == 1);
            foreach (var p in live)
            {
                var player = entries[p];
                bool won = teams ? round.WinningTeam >= 0 && p.Team == round.WinningTeam : p.Place == 1 && firstPlaces == 1;
                bool draw = teams ? round.WinningTeam < 0 : p.Place == 1 && firstPlaces > 1;
                ApplyMatchStatsLocked(player, p, won, draw);
                player.EloRating = Math.Max(0, player.EloRating + p.EloChange);
                _leaderboard[player.PlayerId] = player;
                snapshots.Add(ClonePlayer(player));
            }

            // Old clients read Player1 / Player2 / Winner (1, 2, 0 = draw).
            match.Player1 = ClonePlayerMatchData(match.Players[0]);
            match.Player2 = ClonePlayerMatchData(match.Players[1]);
            match.Winner = match.Player1.Place < match.Player2.Place ? 1 : match.Player2.Place < match.Player1.Place ? 2 : 0;

            _matchHistory.Insert(0, match);
            if (_matchHistory.Count > MaxStoredMatches)
            {
                var dropped = _matchHistory[^1];
                _matchHistory.RemoveAt(_matchHistory.Count - 1);
                if (!string.IsNullOrWhiteSpace(dropped.MatchId))
                    _recordedMatchIds.Remove(dropped.MatchId);
            }
            matchSnapshot = CloneMatch(match);
            SaveData();
        }

        _mongoWriteQueue?.Writer.TryWrite(async () =>
        {
            await InsertMatchToMongoAsync(matchSnapshot);
            foreach (var snap in snapshots)
                await UpsertPlayerToMongoAsync(snap);
        });
        Console.WriteLine($"[Leaderboard] Recorded {round.Mode} round ({match.MatchId}) on {match.LevelName}: " +
                          string.Join(", ", match.Players.Select(p => $"{p.Username} #{p.Place} {p.EloChange:+0;-0;0}")));
        return true;
    }

    private LeaderboardEntryPacket GetOrCreatePlayerLocked(PlayerMatchData playerData)
    {
        string key = ResolvePlayerKey(playerData);
        if (_leaderboard.TryGetValue(key, out var existing)) return existing;
        return new LeaderboardEntryPacket
        {
            PlayerId = key,
            Username = string.IsNullOrWhiteSpace(playerData.Username) ? "Player" : playerData.Username.Trim(),
            AvatarIndex = playerData.AvatarIndex,
            ColorHue = playerData.ColorHue,
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            EloRating = 1000,
        };
    }

    private static void ApplyMatchStatsLocked(LeaderboardEntryPacket player, PlayerMatchData data, bool won, bool draw)
    {
        player.TotalScore += data.Score;
        player.LastPlayedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (won) player.Wins++;
        else if (draw) player.Draws++;
        else player.Losses++;
        if (data.Score > player.HighestScore) player.HighestScore = data.Score;
        if (data.MaxCombo > player.HighestCombo) player.HighestCombo = data.MaxCombo;
        if (data.Accuracy > player.BestAccuracy) player.BestAccuracy = data.Accuracy;
        if (!string.IsNullOrWhiteSpace(data.Username)) player.Username = data.Username.Trim();
        player.ColorHue = data.ColorHue;
    }

    private void EnqueueMongoPlayerUpsert(LeaderboardEntryPacket playerSnapshot)
    {
        _mongoWriteQueue?.Writer.TryWrite(() => UpsertPlayerToMongoAsync(playerSnapshot));
    }

    private void EnqueueMongoMatchWrite(
        MatchHistoryPacket matchSnapshot,
        LeaderboardEntryPacket p1Snapshot,
        LeaderboardEntryPacket p2Snapshot)
    {
        _mongoWriteQueue?.Writer.TryWrite(async () =>
        {
            await InsertMatchToMongoAsync(matchSnapshot);
            await UpsertPlayerToMongoAsync(p1Snapshot);
            if (!string.Equals(p2Snapshot.PlayerId, p1Snapshot.PlayerId, StringComparison.Ordinal))
            {
                await UpsertPlayerToMongoAsync(p2Snapshot);
            }
        });
    }

    private async Task UpsertPlayerToMongoAsync(LeaderboardEntryPacket player)
    {
        if (_leaderboardCollection == null || string.IsNullOrWhiteSpace(player.PlayerId))
            return;

        var filter = Builders<LeaderboardEntryPacket>.Filter.Eq(e => e.PlayerId, player.PlayerId);
        long createdTs = player.CreatedTimestamp > 0
            ? player.CreatedTimestamp
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var update = Builders<LeaderboardEntryPacket>.Update
            .Set(e => e.PlayerId, player.PlayerId)
            .Set(e => e.Username, player.Username)
            .Set(e => e.AvatarIndex, player.AvatarIndex)
            .Set(e => e.ColorHue, player.ColorHue)
            .Set(e => e.TotalScore, player.TotalScore)
            .Set(e => e.Wins, player.Wins)
            .Set(e => e.Losses, player.Losses)
            .Set(e => e.Draws, player.Draws)
            .Set(e => e.HighestScore, player.HighestScore)
            .Set(e => e.HighestCombo, player.HighestCombo)
            .Set(e => e.BestAccuracy, player.BestAccuracy)
            .Set(e => e.EloRating, player.EloRating)
            .Set(e => e.LastPlayedTimestamp, player.LastPlayedTimestamp)
            .SetOnInsert(e => e.CreatedTimestamp, createdTs);

        await _leaderboardCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
        _lastMongoError = null;
        Console.WriteLine($"[Leaderboard] MongoDB upserted player: {player.Username} ({player.PlayerId}, ELO={player.EloRating})");
    }

    private async Task InsertMatchToMongoAsync(MatchHistoryPacket match)
    {
        if (_matchHistoryCollection == null)
            return;

        try
        {
            await _matchHistoryCollection.InsertOneAsync(match);
            _lastMongoError = null;
            Console.WriteLine($"[Leaderboard] MongoDB saved match: {match.MatchId}");
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Already persisted cleanly (idempotent retry / seeding)
        }
    }

    private static string ResolvePlayerKey(PlayerMatchData playerData)
    {
        if (!string.IsNullOrWhiteSpace(playerData.PlayerId))
            return playerData.PlayerId.Trim();

        string cleanName = string.IsNullOrWhiteSpace(playerData.Username) ? "unknown" : playerData.Username.Trim().ToLowerInvariant();
        return $"name:{cleanName}";
    }

    /// <summary>
    /// Update player stats based on match result and return a detached snapshot for background persistence.
    /// </summary>
    private LeaderboardEntryPacket UpdatePlayerFromMatch(PlayerMatchData playerData, bool won, bool draw)
    {
        string playerKey = ResolvePlayerKey(playerData);
        string displayName = string.IsNullOrWhiteSpace(playerData.Username) ? "Player" : playerData.Username.Trim();

        // Get or create player entry
        if (!_leaderboard.TryGetValue(playerKey, out var player))
        {
            player = new LeaderboardEntryPacket
            {
                PlayerId = playerKey,
                Username = displayName,
                AvatarIndex = playerData.AvatarIndex,
                ColorHue = playerData.ColorHue,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EloRating = 1000
            };
        }

        // Update stats
        player.TotalScore += playerData.Score;
        player.LastPlayedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (won)
        {
            player.Wins++;
            player.EloRating += 25; // Simple ELO system
        }
        else if (draw)
        {
            player.Draws++;
        }
        else
        {
            player.Losses++;
            player.EloRating = Math.Max(0, player.EloRating - 15);
        }

        // Update bests
        if (playerData.Score > player.HighestScore)
            player.HighestScore = playerData.Score;
        if (playerData.MaxCombo > player.HighestCombo)
            player.HighestCombo = playerData.MaxCombo;
        if (playerData.Accuracy > player.BestAccuracy)
            player.BestAccuracy = playerData.Accuracy;

        // Update profile info
        player.Username = displayName;
        player.AvatarIndex = playerData.AvatarIndex;
        player.ColorHue = playerData.ColorHue;

        _leaderboard[player.PlayerId] = player;
        return ClonePlayer(player);
    }

    private static LeaderboardEntryPacket ClonePlayer(LeaderboardEntryPacket source) => new()
    {
        PlayerId = source.PlayerId,
        Username = source.Username,
        AvatarIndex = source.AvatarIndex,
        ColorHue = source.ColorHue,
        TotalScore = source.TotalScore,
        Wins = source.Wins,
        Losses = source.Losses,
        Draws = source.Draws,
        HighestScore = source.HighestScore,
        HighestCombo = source.HighestCombo,
        BestAccuracy = source.BestAccuracy,
        EloRating = source.EloRating,
        LastPlayedTimestamp = source.LastPlayedTimestamp,
        CreatedTimestamp = source.CreatedTimestamp
    };

    private static MatchHistoryPacket CloneMatch(MatchHistoryPacket source) => new()
    {
        MatchId = source.MatchId,
        LevelName = source.LevelName,
        Player1 = ClonePlayerMatchData(source.Player1),
        Player2 = ClonePlayerMatchData(source.Player2),
        Winner = source.Winner,
        DurationSeconds = source.DurationSeconds,
        Timestamp = source.Timestamp,
        Mode = source.Mode,
        WinningTeam = source.WinningTeam,
        Players = (source.Players ?? new List<PlayerMatchData>()).Select(ClonePlayerMatchData).ToList()
    };

    private static PlayerMatchData ClonePlayerMatchData(PlayerMatchData source) => new()
    {
        PlayerId = source.PlayerId,
        Username = source.Username,
        AvatarIndex = source.AvatarIndex,
        ColorHue = source.ColorHue,
        Score = source.Score,
        Accuracy = source.Accuracy,
        MaxCombo = source.MaxCombo,
        Misses = source.Misses,
        Disconnected = source.Disconnected,
        Place = source.Place,
        Team = source.Team,
        Side = source.Side,
        EloChange = source.EloChange
    };
    
    /// <summary>
    /// Get total number of players in leaderboard.
    /// </summary>
    public int GetTotalPlayers()
    {
        lock (_lock)
        {
            return _leaderboard.Count;
        }
    }
    
    /// <summary>
    /// Get total number of matches in history.
    /// </summary>
    public int GetTotalMatches()
    {
        lock (_lock)
        {
            return _matchHistory.Count;
        }
    }

    /// <summary>
    /// Record or refresh an active/connected player's profile username without polluting ranked stats.
    /// If the player already exists on the global leaderboard, syncs their latest display name to persistence.
    /// </summary>
    public void UpsertConnectedProfile(PlayerProfilePacket profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.PlayerId))
            return;

        string playerId = profile.PlayerId.Trim();
        string cleanName = string.IsNullOrWhiteSpace(profile.Username) ? "Player" : profile.Username.Trim();
        long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        LeaderboardEntryPacket? mongoSyncSnapshot = null;

        lock (_lock)
        {
            int elo = 1000;
            int wins = 0;
            int losses = 0;
            int draws = 0;

            if (_leaderboard.TryGetValue(playerId, out var existing))
            {
                elo = existing.EloRating;
                wins = existing.Wins;
                losses = existing.Losses;
                draws = existing.Draws;

                if (!string.Equals(existing.Username, cleanName, StringComparison.Ordinal) ||
                    existing.ColorHue != profile.ColorHue ||
                    existing.AvatarIndex != profile.AvatarIndex)
                {
                    existing.Username = cleanName;
                    existing.ColorHue = profile.ColorHue;
                    existing.AvatarIndex = profile.AvatarIndex;
                    _leaderboard[playerId] = existing;
                    SaveData();
                    mongoSyncSnapshot = ClonePlayer(existing);
                }
            }

            _connectedProfiles[playerId] = new UserDirectoryEntry
            {
                PlayerId = playerId,
                Username = cleanName,
                AvatarIndex = profile.AvatarIndex,
                ColorHue = profile.ColorHue,
                EloRating = elo,
                Wins = wins,
                Losses = losses,
                Draws = draws,
                LastSeenTimestamp = nowTs
            };
        }

        if (mongoSyncSnapshot != null)
            EnqueueMongoPlayerUpsert(mongoSyncSnapshot);
    }

    private Dictionary<string, UserDirectoryEntry> BuildKnownUsersMapLocked()
    {
        var users = new Dictionary<string, UserDirectoryEntry>(StringComparer.Ordinal);

        foreach (var match in _matchHistory)
        {
            MergeMatchPlayerLocked(users, match.Player1, match.Timestamp);
            MergeMatchPlayerLocked(users, match.Player2, match.Timestamp);
        }

        foreach (var entry in _leaderboard.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.PlayerId)) continue;
            users[entry.PlayerId] = new UserDirectoryEntry
            {
                PlayerId = entry.PlayerId,
                Username = entry.Username,
                AvatarIndex = entry.AvatarIndex,
                ColorHue = entry.ColorHue,
                EloRating = entry.EloRating,
                Wins = entry.Wins,
                Losses = entry.Losses,
                Draws = entry.Draws,
                LastSeenTimestamp = entry.LastPlayedTimestamp
            };
        }

        foreach (var live in _connectedProfiles.Values)
        {
            if (string.IsNullOrWhiteSpace(live.PlayerId)) continue;
            if (users.TryGetValue(live.PlayerId, out var existing))
            {
                existing.Username = live.Username;
                existing.AvatarIndex = live.AvatarIndex;
                existing.ColorHue = live.ColorHue;
                existing.LastSeenTimestamp = Math.Max(existing.LastSeenTimestamp, live.LastSeenTimestamp);
            }
            else
            {
                users[live.PlayerId] = live.Clone();
            }
        }

        return users;
    }

    private static void MergeMatchPlayerLocked(
        Dictionary<string, UserDirectoryEntry> users,
        PlayerMatchData? player,
        long matchTimestamp)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerId))
            return;

        string id = player.PlayerId.Trim();
        if (!users.TryGetValue(id, out var existing))
        {
            users[id] = new UserDirectoryEntry
            {
                PlayerId = id,
                Username = string.IsNullOrWhiteSpace(player.Username) ? "Player" : player.Username.Trim(),
                AvatarIndex = player.AvatarIndex,
                ColorHue = player.ColorHue,
                LastSeenTimestamp = matchTimestamp
            };
        }
        else if (matchTimestamp >= existing.LastSeenTimestamp && !string.IsNullOrWhiteSpace(player.Username))
        {
            existing.Username = player.Username.Trim();
            existing.LastSeenTimestamp = matchTimestamp;
        }
    }

    public List<UserDirectoryEntry> GetAllUsers(int skip = 0, int take = 200)
    {
        lock (_lock)
        {
            return BuildKnownUsersMapLocked().Values
                .OrderByDescending(u => u.LastSeenTimestamp)
                .ThenByDescending(u => u.EloRating)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 500))
                .Select(u => u.Clone())
                .ToList();
        }
    }

    public int GetTotalKnownUsers()
    {
        lock (_lock)
        {
            return BuildKnownUsersMapLocked().Count;
        }
    }

    public UserDirectoryEntry? GetUserById(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return null;

        string trimmed = playerId.Trim();
        lock (_lock)
        {
            var map = BuildKnownUsersMapLocked();
            return map.TryGetValue(trimmed, out var user) ? user.Clone() : null;
        }
    }
}

public class UserDirectoryEntry
{
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "Player";
    public int AvatarIndex { get; set; }
    public int ColorHue { get; set; } = 180;
    public int EloRating { get; set; } = 1000;
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public long LastSeenTimestamp { get; set; }

    public UserDirectoryEntry Clone() => new()
    {
        PlayerId = PlayerId,
        Username = Username,
        AvatarIndex = AvatarIndex,
        ColorHue = ColorHue,
        EloRating = EloRating,
        Wins = Wins,
        Losses = Losses,
        Draws = Draws,
        LastSeenTimestamp = LastSeenTimestamp
    };
}
