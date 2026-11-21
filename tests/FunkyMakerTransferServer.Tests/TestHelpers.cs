using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FunkyMakerTransferServer.Tests;

internal static class TestSetup
{
    /// <summary>The leaderboard writes JSON next to the binary unless told otherwise.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fmserver-tests-data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("FUNKYMAKER_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("MONGODB_URI", null);
    }
}

/// <summary>Official levels for the tests (the real verified_levels folder is not scanned).</summary>
public sealed class FakeOfficialLevels : IOfficialLevels
{
    public Dictionary<string, OfficialLevel> Levels { get; } = new();
    public OfficialLevel? FindByHash(string hash) => Levels.GetValueOrDefault(hash);

    public OfficialLevel Add(string name)
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var l = new OfficialLevel(name.ToLowerInvariant(), name, name + ".FNMM", hash, 1234);
        Levels[hash] = l;
        return l;
    }
}

public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public string StorageDir { get; } = Path.Combine(Path.GetTempPath(), "fmtransfer-tests-" + Guid.NewGuid().ToString("N"));
    public FakeOfficialLevels Official { get; } = new();
    /// <summary>Extra configuration (e.g. "Transfer:MaxRoomMembers"), applied before the server starts.</summary>
    public Dictionary<string, string> Settings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (k, v) in Settings) builder.UseSetting(k, v);
        builder.UseSetting("Transfer:StorageDirectory", StorageDir);
        builder.UseSetting("Transfer:ProgressIntervalMs", "0");
        builder.UseSetting("Transfer:HttpRequestsPerMinute", "1000");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IOfficialLevels>();
            s.AddSingleton<IOfficialLevels>(Official);
        });
    }

    public RoomRegistry Registry => Services.GetRequiredService<RoomRegistry>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(StorageDir, true); } catch { }
    }
}

public sealed record TestLevel(string Name, byte[] Bytes)
{
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
    public LevelEntry Entry => new() { Hash = Hash, Name = Name, FileName = Name + ".fnmm", Size = Bytes.Length, Stars = 3.5f };

    public static TestLevel Create(string name, int size = 300_000)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        bytes[0] = (byte)'P'; bytes[1] = (byte)'K'; bytes[2] = 3; bytes[3] = 4;
        return new TestLevel(name, bytes);
    }
}

/// <summary>One fake game client: a hub connection that records every server message.</summary>
public sealed class TestPlayer : IAsyncDisposable
{
    private readonly List<(string Method, object Payload)> _messages = new();
    private readonly ServerFactory _factory;

    public HubConnection Hub { get; }
    public string Name { get; }
    public string PlayerId { get; } = Guid.NewGuid().ToString();
    public List<TestLevel> Levels { get; } = new();

    private TestPlayer(ServerFactory factory, string name)
    {
        _factory = factory;
        Name = name;
        var server = factory.Server;
        Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/multiplayer"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        Record<RoomStatePacket>(Protocol.RoomState);
        Record<ChatMessage>(Protocol.Chat);
        Record<KickedPacket>(Protocol.Kicked);
        Record<TransferUploadPacket>(Protocol.TransferUpload);
        Record<TransferDownloadPacket>(Protocol.TransferDownload);
        Record<TransferFailedPacket>(Protocol.TransferFailed);
        Record<MatchInfo>(Protocol.MatchLoading);
        Record<MatchStartPacket>(Protocol.MatchStart);
        Record<RoundResult>(Protocol.MatchResults);
        Record<PlayerStatePacket>(Protocol.PlayerState);
        Record<PlayerHitPacket>(Protocol.PlayerHit);
    }

    private void Record<T>(string method) where T : class =>
        Hub.On<T>(method, p => { lock (_messages) _messages.Add((method, p)); });

    public static async Task<TestPlayer> ConnectAsync(ServerFactory factory, string name, params TestLevel[] levels)
    {
        var p = new TestPlayer(factory, name);
        p.Levels.AddRange(levels);
        await p.Hub.StartAsync();
        var hello = await p.Hub.InvokeAsync<HelloResult>("Hello", new HelloRequest
        {
            PlayerId = p.PlayerId, Username = name, ColorHue = 120, ProtocolVersion = Protocol.Version,
        });
        Assert.True(hello.Ok, hello.Error);
        return p;
    }

    public async Task<string> CreateAsync(RoomSettings? settings = null, string name = "Test room")
    {
        var r = await Hub.InvokeAsync<OpResult>("CreateRoom", new CreateRoomRequest
        {
            Name = name, Settings = settings ?? new RoomSettings { MaxPlayers = 8 }, Levels = Levels.Select(l => l.Entry).ToList(),
        });
        Assert.True(r.Ok, r.Error);
        return r.RoomId;
    }

    public Task<OpResult> TryJoinAsync(string roomId, string password = "") =>
        Hub.InvokeAsync<OpResult>("JoinRoom", new JoinRoomRequest
        {
            RoomId = roomId, Password = password, Levels = Levels.Select(l => l.Entry).ToList(),
        });

    public async Task JoinAsync(string roomId, string password = "")
    {
        var r = await TryJoinAsync(roomId, password);
        Assert.True(r.Ok, r.Error);
    }

    public Task<OpResult> PickLocalAsync(TestLevel level) =>
        Hub.InvokeAsync<OpResult>("SelectSong", new SongSelection { Source = Protocol.SourceLocal, Hash = level.Hash, Stars = 4.2f });

    public Task<OpResult> ReadyAsync(bool ready = true) => Hub.InvokeAsync<OpResult>("SetReady", ready);

    public List<T> All<T>(string method)
    {
        lock (_messages)
            return _messages.Where(m => m.Method == method).Select(m => (T)m.Payload).ToList();
    }

    public T? Latest<T>(string method) where T : class => All<T>(method).LastOrDefault();

    /// <summary>Latest room state (merged: Levels / History null = unchanged).</summary>
    public RoomStatePacket State
    {
        get
        {
            var all = All<RoomStatePacket>(Protocol.RoomState);
            var last = all.Last();
            last.Levels ??= all.LastOrDefault(s => s.Levels != null)?.Levels;
            last.History ??= all.LastOrDefault(s => s.History != null)?.History;
            return last;
        }
    }

    public async Task<T> WaitFor<T>(string method, Func<T, bool>? predicate = null, int timeoutMs = 5000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var hit = All<T>(method).LastOrDefault(m => predicate?.Invoke(m) ?? true);
            if (hit != null) return hit;
            await Task.Delay(20);
        }
        throw new TimeoutException($"{Name}: no {method} matching the condition within {timeoutMs} ms");
    }

    public Task<RoomStatePacket> WaitState(Func<RoomStatePacket, bool> predicate, int timeoutMs = 5000) =>
        WaitFor(Protocol.RoomState, predicate, timeoutMs);

    public Task<ChatMessage> WaitChat(string contains, int timeoutMs = 5000) =>
        WaitFor<ChatMessage>(Protocol.Chat, c => c.Text.Contains(contains, StringComparison.OrdinalIgnoreCase), timeoutMs);

    public async Task<HttpResponseMessage> UploadAsync(TransferUploadPacket t, byte[] bytes)
    {
        using var http = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{t.TransferId}/file")
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Headers.Add(Protocol.TokenHeader, t.Token);
        return await http.SendAsync(req);
    }

    public async Task<byte[]> DownloadAsync(TransferDownloadPacket t)
    {
        using var http = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/transfers/{t.TransferId}/file");
        req.Headers.Add(Protocol.TokenHeader, t.Token);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Hub.DisposeAsync(); } catch { }
    }
}
