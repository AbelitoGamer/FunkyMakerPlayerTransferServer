using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;

namespace FunkyMakerTransferServer.Tests;

public sealed class ReplayExchangeTests : IDisposable
{
    private readonly ServerFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private static readonly string Level = new('a', 64);

    [Fact]
    public async Task Upload_then_opponent_downloads_it()
    {
        using var http = _factory.CreateClient();
        var body = Gzip("{\"Frames\":[]}");
        var put = await http.PutAsync($"/api/replays?player={A}&opponent={B}&level={Level}", new ByteArrayContent(body));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await http.GetAsync($"/api/replays?player={A}&opponent={B}&level={Level}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());

        // Other direction / other level: nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/replays?player={B}&opponent={A}&level={Level}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/replays?player={A}&opponent={B}&level={new string('b', 64)}")).StatusCode);
    }

    [Fact]
    public async Task Rejects_bad_ids_and_non_gzip()
    {
        using var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/api/replays?player=../x&opponent={B}&level={Level}", new ByteArrayContent(Gzip("x")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/api/replays?player={A}&opponent={B}&level={Level}", new ByteArrayContent(Encoding.UTF8.GetBytes("plain")))).StatusCode);
    }
}
