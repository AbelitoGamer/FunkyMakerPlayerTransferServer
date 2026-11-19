using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace FunkyMakerTransferServer.Tests;

public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public string StorageDir { get; } = Path.Combine(Path.GetTempPath(), "fmtransfer-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Transfer:StorageDirectory", StorageDir);
        builder.UseSetting("Transfer:ProgressIntervalMs", "0");
        builder.UseSetting("Transfer:HttpRequestsPerMinute", "1000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(StorageDir, true); } catch { }
    }
}

public sealed record TestLevel(string Name, byte[] Bytes)
{
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
    public LevelEntry Entry => new() { Hash = Hash, Name = Name, FileName = Name + ".fnmm", Size = Bytes.Length };

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

    private TestPlayer(ServerFactory factory, string name)
    {
        _factory = factory;
        Name = name;
        var server = factory.Server;
        Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/transfer"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        Record<RoomStatePacket>(Protocol.RoomState);
        Record<QueueStatePacket>(Protocol.QueueState);
        Record<TransferStartedPacket>(Protocol.TransferStarted);
        Record<TransferProgressPacket>(Protocol.TransferProgress);
        Record<ReadyToDownloadPacket>(Protocol.ReadyToDownload);
        Record<TransferCancelledPacket>(Protocol.TransferCancelled);
        Record<MatchReadyPacket>(Protocol.MatchReady);
    }

    private void Record<T>(string method) where T : class =>
        Hub.On<T>(method, p => { lock (_messages) _messages.Add((method, p)); });

    public static async Task<TestPlayer> JoinAsync(ServerFactory factory, string name, string room, params TestLevel[] levels)
    {
        var p = new TestPlayer(factory, name);
        await p.Hub.StartAsync();
        var result = await p.Hub.InvokeAsync<JoinRoomResult>("JoinRoom", new JoinRoomRequest
        {
            RoomCode = room,
            PlayerId = Guid.NewGuid().ToString(),
            Username = name,
            ProtocolVersion = Protocol.Version,
            Levels = levels.Select(l => l.Entry).ToList(),
        });
        Assert.True(result.Ok, result.Error);
        return p;
    }

    public async Task<QueueResult> QueueAsync(TestLevel level)
    {
        var r = await Hub.InvokeAsync<QueueResult>("JoinQueue", level.Hash);
        Assert.True(r.Ok, r.Error);
        return r;
    }

    public List<T> All<T>(string method)
    {
        lock (_messages)
            return _messages.Where(m => m.Method == method).Select(m => (T)m.Payload).ToList();
    }

    public T? Latest<T>(string method) where T : class => All<T>(method).LastOrDefault();

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

    public async Task<HttpResponseMessage> UploadAsync(TransferStartedPacket t, byte[] bytes)
    {
        using var http = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{t.TransferId}/file")
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Headers.Add(Protocol.TokenHeader, t.Token);
        return await http.SendAsync(req);
    }

    public async Task<byte[]> DownloadAsync(TransferStartedPacket t)
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
