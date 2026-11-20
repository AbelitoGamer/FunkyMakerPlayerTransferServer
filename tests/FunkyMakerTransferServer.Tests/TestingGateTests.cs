using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace FunkyMakerTransferServer.Tests;

public sealed class TestingGateTests
{
    private sealed class ClosedFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Testing:Open", "false");
            builder.UseSetting("Testing:Message", "Thanks for testing!");
        }
    }

    private static async Task<JsonElement> GetGate(HttpClient http)
    {
        var res = await http.GetAsync("/api/testing");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString() ?? "");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Open_by_default()
    {
        using var factory = new ServerFactory();
        var json = await GetGate(factory.CreateClient());
        Assert.True(json.GetProperty("open").GetBoolean());
        Assert.Equal("", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Closed_with_message_from_config()
    {
        using var factory = new ClosedFactory();
        var json = await GetGate(factory.CreateClient());
        Assert.False(json.GetProperty("open").GetBoolean());
        Assert.Equal("Thanks for testing!", json.GetProperty("message").GetString());
    }
}
