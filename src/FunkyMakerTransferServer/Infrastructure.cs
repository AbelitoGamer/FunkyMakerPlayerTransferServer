using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace FunkyMakerTransferServer;

/// <summary>Temporary on-disk storage for uploaded levels. Wiped on start (Render disks are ephemeral anyway).</summary>
public sealed class TransferStore
{
    private readonly ILogger<TransferStore> _log;

    public TransferStore(IOptions<TransferOptions> options, ILogger<TransferStore> log)
    {
        _log = log;
        string dir = options.Value.StorageDirectory;
        Directory = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Path.GetTempPath(), "funkymaker-transfer")
            : dir;
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not clear storage directory {Dir}", Directory);
        }
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string NewUploadPath(string transferId) =>
        Path.Combine(Directory, $"{transferId}-{Guid.NewGuid():N}.bin");

    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}

/// <summary>Runs <see cref="Effects"/> outside the registry lock.</summary>
public sealed class EffectRunner(IHubContext<TransferHub> hub, TransferStore store, ILogger<EffectRunner> log)
{
    public async Task RunAsync(Effects fx)
    {
        foreach (var cts in fx.ToCancel)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        foreach (var path in fx.FilesToDelete)
            store.TryDelete(path);
        foreach (var m in fx.Messages)
        {
            try
            {
                await hub.Clients.Client(m.ConnectionId).SendAsync(m.Method, m.Payload);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Send {Method} to {Conn} failed", m.Method, m.ConnectionId);
            }
        }
    }
}

/// <summary>Every few seconds: time out stalled transfers and expire cached files.</summary>
public sealed class SweepService(RoomRegistry registry, EffectRunner runner, ILogger<SweepService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var fx = new Effects();
                registry.Sweep(fx);
                await runner.RunAsync(fx);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Sweep failed");
            }
        }
    }
}
