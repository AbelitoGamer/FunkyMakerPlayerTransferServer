using System.Text.RegularExpressions;

namespace FunkyMakerTransferServer;

/// <summary>
/// Online match replays: after a match each player uploads their own recording (a small gzip JSON
/// "track"), keyed by (uploader, opponent, level hash); the opponent then downloads it. Kept in
/// memory for <see cref="Ttl"/>; the newest upload for a key wins. The game server is not involved.
/// <list type="bullet">
/// <item><c>PUT /api/replays?player=A&amp;opponent=B&amp;level=H</c> — body: A's track.</item>
/// <item><c>GET /api/replays?player=A&amp;opponent=B&amp;level=H</c> — A's latest track vs B on H, or 404.</item>
/// </list>
/// Player ids are the game's PlayerIds (GUIDs); level is the level file's SHA-256.
/// </summary>
public sealed partial class ReplayExchange
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    public const int MaxTrackBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 4000;

    private readonly object _lock = new();
    private readonly Dictionary<string, (byte[] Data, DateTimeOffset At)> _tracks = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ReplayExchange(TimeProvider time) => _time = time;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    public static string? Key(string? player, string? opponent, string? level)
    {
        level = level?.Trim().ToLowerInvariant();
        if (player == null || opponent == null || level == null) return null;
        if (!IdRegex().IsMatch(player) || !IdRegex().IsMatch(opponent) || !HashRegex().IsMatch(level)) return null;
        return $"{player}|{opponent}|{level}";
    }

    public void Put(string key, byte[] data)
    {
        lock (_lock)
        {
            SweepLocked();
            if (_tracks.Count >= MaxEntries)
            {
                var oldest = _tracks.MinBy(kv => kv.Value.At).Key;
                _tracks.Remove(oldest);
            }
            _tracks[key] = (data, _time.GetUtcNow());
        }
    }

    public byte[]? Get(string key)
    {
        lock (_lock)
        {
            SweepLocked();
            return _tracks.TryGetValue(key, out var entry) ? entry.Data : null;
        }
    }

    public int Count
    {
        get { lock (_lock) return _tracks.Count; }
    }

    private void SweepLocked()
    {
        var cutoff = _time.GetUtcNow() - Ttl;
        foreach (var k in _tracks.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _tracks.Remove(k);
    }

    public static async Task<IResult> PutAsync(HttpContext ctx, ReplayExchange exchange)
    {
        var q = ctx.Request.Query;
        string? key = Key(q["player"], q["opponent"], q["level"]);
        if (key == null) return Results.BadRequest("Invalid player, opponent or level.");
        if (ctx.Request.ContentLength is > MaxTrackBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxTrackBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        // Gzip magic: the game always sends a gzip JSON track.
        var data = ms.ToArray();
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b)
            return Results.BadRequest("Expected a gzip replay track.");
        exchange.Put(key, data);
        return Results.Ok();
    }

    public static IResult Get(HttpContext ctx, ReplayExchange exchange)
    {
        var q = ctx.Request.Query;
        string? key = Key(q["player"], q["opponent"], q["level"]);
        if (key == null) return Results.BadRequest("Invalid player, opponent or level.");
        var data = exchange.Get(key);
        return data == null ? Results.NotFound() : Results.Bytes(data, "application/gzip");
    }
}
