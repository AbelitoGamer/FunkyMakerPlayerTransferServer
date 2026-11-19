using System.Reflection;
using System.Threading.RateLimiting;
using FunkyMakerTransferServer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Render (and most PaaS hosts) pass the port to listen on in $PORT.
string? port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<TransferOptions>(builder.Configuration.GetSection("Transfer"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<TransferStore>();
builder.Services.AddSingleton<EffectRunner>();
builder.Services.AddSingleton<ReplayExchange>();
builder.Services.AddHostedService<SweepService>();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024 * 1024; // level lists can be a few hundred KB
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Render terminates TLS in front of the app; trust its X-Forwarded-* headers for client IPs.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx =>
    {
        int limit = ctx.RequestServices.GetRequiredService<IOptions<TransferOptions>>().Value.HttpRequestsPerMinute;
        string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRateLimiter();

string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet("/", () => Results.Text("FunkyMaker level transfer server. See /health."));
app.MapGet("/health", (RoomRegistry registry) => Results.Ok(new
{
    status = "ok",
    version,
    protocol = Protocol.Version,
    maxUploadBytes = registry.MaxUploadBytes,
    stats = registry.GetStats(),
}));

app.MapHub<TransferHub>("/transfer");

var api = app.MapGroup("/api/transfers").RequireRateLimiting("api");
api.MapPut("/{id}/file", TransferEndpoints.UploadAsync);
api.MapGet("/{id}/file", TransferEndpoints.DownloadAsync);

var replays = app.MapGroup("/api/replays").RequireRateLimiting("api");
replays.MapPut("/", ReplayExchange.PutAsync);
replays.MapGet("/", ReplayExchange.Get);

app.Run();

/// <summary>Exposed for WebApplicationFactory in the tests.</summary>
public partial class Program;
