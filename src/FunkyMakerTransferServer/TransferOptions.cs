namespace FunkyMakerTransferServer;

/// <summary>
/// Limits and timeouts. Bound from the "Transfer" config section, so on Render they can be set
/// with environment variables such as <c>Transfer__MaxUploadMegabytes=150</c>.
/// </summary>
public sealed class TransferOptions
{
    public int MaxUploadMegabytes { get; set; } = 250;
    /// <summary>
    /// Highest player limit hosts can give their rooms (env <c>Transfer__MaxRoomMembers</c>), clamped
    /// to 2..<see cref="Protocol.HardMaxPlayers"/>. It used to be unused (always 8).
    /// </summary>
    public int MaxRoomMembers { get; set; } = Protocol.MaxPlayers;
    public int MaxLevelsPerMember { get; set; } = 5000; // osu! sets list every 4K difficulty
    /// <summary>A transfer with no progress for this long is cancelled.</summary>
    public int StallTimeoutSeconds { get; set; } = 300;
    /// <summary>
    /// Extra time after Kestrel finishes writing a download response for the client to finish
    /// draining any reverse-proxy buffer, verifying SHA-256 and reporting CompleteDownload.
    /// </summary>
    public int DownloadCompletionGraceSeconds { get; set; } = 600;
    /// <summary>Max time a match waits in PhaseLoading before slow loaders become spectators.</summary>
    public int LoadingTimeoutSeconds { get; set; } = 180;
    /// <summary>How long an uploaded file stays reusable for the same room after its last use.</summary>
    public int CacheMinutes { get; set; } = 15;
    /// <summary>Minimum gap between progress packets per transfer.</summary>
    public int ProgressIntervalMs { get; set; } = 200;
    /// <summary>HTTP requests per minute per IP on /api (high enough for chunked uploads).</summary>
    public int HttpRequestsPerMinute { get; set; } = 360;
    /// <summary>Preferred chunk size (MB) advertised to clients for chunked uploads (clamped 1..16 MB).</summary>
    public int UploadChunkMegabytes { get; set; } = 4;
    /// <summary>Where uploads are stored; empty = a folder under the system temp directory.</summary>
    public string StorageDirectory { get; set; } = "";

    public long MaxUploadBytes => MaxUploadMegabytes * 1024L * 1024L;
    public int UploadChunkBytes => Math.Clamp(UploadChunkMegabytes, 1, 16) * 1024 * 1024;
}
