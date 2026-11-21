namespace FunkyMakerTransferServer;

/// <summary>
/// Limits and timeouts. Bound from the "Transfer" config section, so on Render they can be set
/// with environment variables such as <c>Transfer__MaxUploadMegabytes=150</c>.
/// </summary>
public sealed class TransferOptions
{
    public int MaxUploadMegabytes { get; set; } = 100;
    /// <summary>
    /// Highest player limit hosts can give their rooms (env <c>Transfer__MaxRoomMembers</c>), clamped
    /// to 2..<see cref="Protocol.HardMaxPlayers"/>. It used to be unused (always 8).
    /// </summary>
    public int MaxRoomMembers { get; set; } = Protocol.MaxPlayers;
    public int MaxLevelsPerMember { get; set; } = 2000;
    /// <summary>A transfer with no progress for this long is cancelled.</summary>
    public int StallTimeoutSeconds { get; set; } = 60;
    /// <summary>How long an uploaded file stays reusable for the same room after its last use.</summary>
    public int CacheMinutes { get; set; } = 15;
    /// <summary>Minimum gap between progress packets per transfer.</summary>
    public int ProgressIntervalMs { get; set; } = 200;
    /// <summary>HTTP requests per minute per IP on /api.</summary>
    public int HttpRequestsPerMinute { get; set; } = 60;
    /// <summary>Where uploads are stored; empty = a folder under the system temp directory.</summary>
    public string StorageDirectory { get; set; } = "";

    public long MaxUploadBytes => MaxUploadMegabytes * 1024L * 1024L;
}
