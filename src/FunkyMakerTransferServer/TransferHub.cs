using Microsoft.AspNetCore.SignalR;

namespace FunkyMakerTransferServer;

/// <summary>
/// Real-time side of the protocol (endpoint <c>/transfer</c>). File bytes never go through the hub;
/// they use the HTTP endpoints in <see cref="TransferEndpoints"/>.
/// </summary>
public sealed class TransferHub(RoomRegistry registry, EffectRunner runner) : Hub
{
    public async Task<JoinRoomResult> JoinRoom(JoinRoomRequest request)
    {
        var fx = new Effects();
        var result = registry.JoinRoom(Context.ConnectionId, request ?? new JoinRoomRequest(), fx);
        await runner.RunAsync(fx);
        return result;
    }

    public Task UpdateLevels(List<LevelEntry> levels) =>
        Run(fx => registry.UpdateLevels(Context.ConnectionId, levels, fx));

    public Task LeaveRoom() =>
        Run(fx => registry.LeaveRoom(Context.ConnectionId, fx));

    public async Task<QueueResult> JoinQueue(string hash)
    {
        var fx = new Effects();
        var result = registry.JoinQueue(Context.ConnectionId, hash, fx);
        await runner.RunAsync(fx);
        return result;
    }

    public Task LeaveQueue() =>
        Run(fx => registry.LeaveQueue(Context.ConnectionId, fx));

    public Task CompleteTransfer(string transferId, bool ok, string? error) =>
        Run(fx => registry.CompleteTransfer(Context.ConnectionId, transferId, ok, error, fx));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Run(fx => registry.LeaveRoom(Context.ConnectionId, fx));
        await base.OnDisconnectedAsync(exception);
    }

    private Task Run(Action<Effects> action)
    {
        var fx = new Effects();
        action(fx);
        return runner.RunAsync(fx);
    }
}
