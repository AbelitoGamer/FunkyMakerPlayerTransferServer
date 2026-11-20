using FunkyMakerServer.Models;
using FunkyMakerServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace FunkyMakerTransferServer;

/// <summary>
/// The online hub (endpoint <c>/multiplayer</c>): session, room browser, waiting room, round flow and
/// the in-match relay. Level file bytes never go through the hub; they use <see cref="TransferEndpoints"/>.
/// </summary>
public sealed class RoomHub(RoomRegistry registry, EffectRunner runner, LeaderboardService leaderboard) : Hub
{
    // ── Session ──────────────────────────────────────────────────────────────

    public async Task<HelloResult> Hello(HelloRequest request)
    {
        request ??= new HelloRequest();
        var fx = new Effects();
        var result = registry.Hello(Context.ConnectionId, request, fx);
        await runner.RunAsync(fx);
        if (result.Ok)
        {
            result.ServerVersion = ServerVersion.Value;
            leaderboard.UpsertConnectedProfile(new PlayerProfilePacket
            {
                PlayerId = request.PlayerId, Username = request.Username, ColorHue = request.ColorHue,
                ClientVersion = request.ClientVersion,
            });
        }
        return result;
    }

    public Task Ping(PingRequest packet) =>
        Clients.Caller.SendAsync(Protocol.Pong, registry.Ping(Context.ConnectionId, packet?.ClientTimestamp ?? 0));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Run(fx => registry.Disconnect(Context.ConnectionId, fx));
        await base.OnDisconnectedAsync(exception);
    }

    // ── Rooms ────────────────────────────────────────────────────────────────

    public List<RoomSummary> ListRooms() => registry.ListRooms();

    public Task<OpResult> CreateRoom(CreateRoomRequest request) =>
        RunResult(fx => registry.CreateRoom(Context.ConnectionId, request ?? new CreateRoomRequest(), fx));

    public Task<OpResult> JoinRoom(JoinRoomRequest request) =>
        RunResult(fx => registry.JoinRoom(Context.ConnectionId, request ?? new JoinRoomRequest(), fx));

    public Task LeaveRoom() => Run(fx => registry.LeaveRoom(Context.ConnectionId, fx));

    public Task UpdateLevels(List<LevelEntry> levels) => Run(fx => registry.UpdateLevels(Context.ConnectionId, levels, fx));

    public Task<OpResult> UpdateSettings(RoomSettings settings) =>
        RunResult(fx => registry.UpdateSettings(Context.ConnectionId, settings ?? new RoomSettings(), fx));

    public Task<OpResult> Kick(string playerId) => RunResult(fx => registry.Kick(Context.ConnectionId, playerId ?? "", fx));

    public Task SendChat(string text) => Run(fx => registry.Chat(Context.ConnectionId, text, fx));

    public Task SetSpectate(bool spectate) => Run(fx => registry.SetSpectate(Context.ConnectionId, spectate, fx));

    // ── Round ────────────────────────────────────────────────────────────────

    public Task<OpResult> SelectSong(SongSelection selection) =>
        RunResult(fx => registry.SelectSong(Context.ConnectionId, selection ?? new SongSelection(), fx));

    public Task<OpResult> SetReady(bool ready) => RunResult(fx => registry.SetReady(Context.ConnectionId, ready, fx));

    public Task SetLevelStatus(LevelStatusUpdate update) =>
        Run(fx => registry.SetLevelStatus(Context.ConnectionId, update ?? new LevelStatusUpdate(), fx));

    public Task<OpResult> VoteSide(int side) => RunResult(fx => registry.VoteSide(Context.ConnectionId, side, fx));

    public Task CompleteDownload(string transferId, bool ok, string? error) =>
        Run(fx => registry.CompleteDownload(Context.ConnectionId, transferId ?? "", ok, error, fx));

    // ── Match ────────────────────────────────────────────────────────────────

    public Task MatchLoaded(string matchId) => Run(fx => registry.MatchLoaded(Context.ConnectionId, matchId ?? "", fx));

    public Task SendState(PlayerStatePacket packet)
    {
        if (packet == null) return Task.CompletedTask;
        registry.RecordState(Context.ConnectionId, packet);
        return RelayAsync(Protocol.PlayerState, id => { packet.PlayerId = id; return packet; });
    }

    public Task SendHit(PlayerHitPacket packet) =>
        packet == null ? Task.CompletedTask : RelayAsync(Protocol.PlayerHit, id => { packet.PlayerId = id; return packet; });

    public Task SendKeyPress(PlayerKeyPressPacket packet) =>
        packet == null ? Task.CompletedTask : RelayAsync(Protocol.PlayerKeyPress, id => { packet.PlayerId = id; return packet; });

    public Task FinishMatch(FinishMatchRequest request) =>
        Run(fx => registry.FinishMatch(Context.ConnectionId, request ?? new FinishMatchRequest(), fx));

    // ── Leaderboard (same packets as the old game server) ────────────────────

    public Task GetLeaderboard(GetLeaderboardPacket packet)
    {
        packet ??= new GetLeaderboardPacket();
        return Clients.Caller.SendAsync("LeaderboardResponse", new LeaderboardResponsePacket
        {
            Entries = leaderboard.GetTopPlayers(Math.Max(0, packet.Skip), Math.Clamp(packet.Take, 1, 200)),
            TotalPlayers = leaderboard.GetTotalPlayers(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public Task GetMatchHistory(GetMatchHistoryPacket packet)
    {
        packet ??= new GetMatchHistoryPacket();
        return Clients.Caller.SendAsync("MatchHistoryResponse", new MatchHistoryResponsePacket
        {
            Matches = leaderboard.GetRecentMatches(packet.PlayerId, Math.Max(0, packet.Skip), Math.Clamp(packet.Take, 1, 200)),
            TotalMatches = leaderboard.GetTotalMatches(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task RelayAsync(string method, Func<string, object> payload)
    {
        if (!registry.Relay(Context.ConnectionId, out string playerId, out var targets) || targets.Count == 0) return;
        await Clients.Clients(targets).SendAsync(method, payload(playerId));
    }

    private Task Run(Action<Effects> action)
    {
        var fx = new Effects();
        action(fx);
        return runner.RunAsync(fx);
    }

    private async Task<OpResult> RunResult(Func<Effects, OpResult> action)
    {
        var fx = new Effects();
        var result = action(fx);
        await runner.RunAsync(fx);
        return result;
    }
}

public static class ServerVersion
{
    public static readonly string Value =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary><see cref="IOfficialLevels"/> over the official server's verified_levels scanner.</summary>
public sealed class OfficialLevelsAdapter(LevelService levels) : IOfficialLevels
{
    public OfficialLevel? FindByHash(string hash)
    {
        var info = levels.ScanVerifiedLevels()
            .FirstOrDefault(l => string.Equals(l.Hash, hash, StringComparison.OrdinalIgnoreCase));
        return info == null ? null : new OfficialLevel(info.Id, info.Name, info.Filename, info.Hash.ToLowerInvariant(), info.FileSize);
    }
}
