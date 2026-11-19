namespace FunkyMakerTransferServer;

// Wire types shared with the game client (FunkyMakerPlayer/Multiplayer/Transfer/TransferProtocol.cs).
// Keep both files in sync and bump ProtocolVersion on breaking changes.

public static class Protocol
{
    public const int Version = 1;

    // Hub methods the server calls on clients.
    public const string RoomState = "RoomState";
    public const string QueueState = "QueueState";
    public const string TransferStarted = "TransferStarted";
    public const string TransferProgress = "TransferProgress";
    public const string ReadyToDownload = "ReadyToDownload";
    public const string TransferCancelled = "TransferCancelled";
    public const string MatchReady = "MatchReady";

    // Transfer roles / phases (strings on the wire).
    public const string RoleUpload = "upload";
    public const string RoleDownload = "download";
    public const string PhaseUpload = "upload";
    public const string PhaseDownload = "download";
    public const string PhaseVerify = "verify";

    public const string TokenHeader = "X-Transfer-Token";
}

/// <summary>One level a player has in their local levels folder.</summary>
public sealed class LevelEntry
{
    /// <summary>Lower-case hex SHA-256 of the level file.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Display name (usually the file name without extension).</summary>
    public string Name { get; set; } = "";
    /// <summary>File name with extension (.fnmm / .zip / .rar).</summary>
    public string FileName { get; set; } = "";
    public long Size { get; set; }
}

public sealed class JoinRoomRequest
{
    public string RoomCode { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public List<LevelEntry> Levels { get; set; } = new();
}

public sealed class JoinRoomResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public long MaxUploadBytes { get; set; }
}

public sealed class QueueResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>A level as one specific player sees the room (sent per player).</summary>
public sealed class RoomLevel
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    /// <summary>The receiving player has this exact file.</summary>
    public bool YouHave { get; set; }
    /// <summary>How many other players in the room have this exact file.</summary>
    public int OthersHave { get; set; }
    /// <summary>How many players (including you) are queued for this level.</summary>
    public int Waiting { get; set; }
}

public sealed class RoomStatePacket
{
    public string RoomCode { get; set; } = "";
    public int Members { get; set; }
    public List<RoomLevel> Levels { get; set; } = new();
}

public sealed class QueueStatePacket
{
    /// <summary>Hash the player is queued for, or empty when not queued.</summary>
    public string Hash { get; set; } = "";
    public bool YouHave { get; set; }
    /// <summary>Players queued for this level, including you.</summary>
    public int Waiting { get; set; }
    /// <summary>Players in the room (queued or not) that have the file.</summary>
    public int HoldersInRoom { get; set; }
}

public sealed class TransferStartedPacket
{
    public string TransferId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    /// <summary>"upload" (you have the level) or "download".</summary>
    public string Role { get; set; } = "";
    /// <summary>Starting phase: "upload", or "download" when the server already has the file.</summary>
    public string Phase { get; set; } = "";
    /// <summary>Secret for the HTTP endpoint matching your role.</summary>
    public string Token { get; set; } = "";
    public string OpponentName { get; set; } = "";
}

public sealed class TransferProgressPacket
{
    public string TransferId { get; set; } = "";
    public string Phase { get; set; } = "";
    public long Bytes { get; set; }
    public long Total { get; set; }
}

public sealed class ReadyToDownloadPacket
{
    public string TransferId { get; set; } = "";
}

public sealed class TransferCancelledPacket
{
    public string TransferId { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>True when you were put back in the queue for the same level.</summary>
    public bool Requeued { get; set; }
}

public sealed class MatchReadyPacket
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Private room code to send to the game server's FindMatch (unique to this pair).</summary>
    public string MatchCode { get; set; } = "";
    public string OpponentName { get; set; } = "";
}
