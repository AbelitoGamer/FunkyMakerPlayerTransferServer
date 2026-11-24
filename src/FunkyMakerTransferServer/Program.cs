using System.Threading.RateLimiting;
using FunkyMakerServer.Services;
using FunkyMakerTransferServer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

// FunkyMaker online server: rooms + matches (SignalR /multiplayer), level transfers between players,
// replay exchange, and the official game server's HTTP API (leaderboard, users, verified levels).

var builder = WebApplication.CreateBuilder(args);

// Render (and most PaaS hosts) pass the port to listen on in $PORT.
string? port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Slow / mobile uploads and downloads can pause longer than Kestrel's default 5 s data-rate grace
// period, and Render's reverse proxy may buffer request/response bodies. Per-transfer size limits
// and inactivity timeouts are enforced explicitly by TransferEndpoints and RoomRegistry.Sweep.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MinRequestBodyDataRate = null;
    k.Limits.MinResponseDataRate = null;
    k.Limits.MaxRequestBodySize = null;
    k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    k.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
});

builder.Services.Configure<TransferOptions>(builder.Configuration.GetSection("Transfer"));
builder.Services.Configure<TestingOptions>(builder.Configuration.GetSection("Testing"));
builder.Services.AddSingleton(TimeProvider.System);

// Official server services (leaderboard persistence: MongoDB when MONGODB_URI is set, else local JSON).
builder.Services.AddSingleton<HashService>();
builder.Services.AddSingleton<LevelService>();
builder.Services.AddSingleton<LeaderboardService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaderboardService>());
builder.Services.AddSingleton<IOfficialLevels, OfficialLevelsAdapter>();

builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<TransferStore>();
builder.Services.AddSingleton<EffectRunner>();
builder.Services.AddSingleton<ReplayExchange>();
builder.Services.AddHostedService<SweepService>();
builder.Services.AddControllers();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 4 * 1024 * 1024; // level lists (with every osu! difficulty) can be ~1-2 MB
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    // Keep generous headroom so uplink saturation (bufferbloat during big uploads) does not drop
    // the uploader from the room and cancel active transfers; in-match crashes use SilenceTimeout (10 s).
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(90);
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddCors(o => o.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

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
app.UseCors("AllowAll");
app.UseRateLimiter();

app.MapGet("/", () => Results.Redirect("/api/server/info"));
app.MapGet("/health", (RoomRegistry registry) => Results.Ok(new
{
    status = "ok",
    version = ServerVersion.Value,
    protocol = Protocol.Version,
    maxUploadBytes = registry.MaxUploadBytes,
    stats = registry.GetStats(),
}));

// Test-build gate (see TestingOptions): test clients refuse to start unless this says open.
app.MapGet("/api/testing", (HttpContext ctx, IOptionsMonitor<TestingOptions> testing) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var t = testing.CurrentValue;
    return Results.Ok(new { open = t.Open, message = t.Message ?? "" });
});

app.MapControllers();
app.MapHub<RoomHub>("/multiplayer");

var api = app.MapGroup("/api/transfers").RequireRateLimiting("api");
api.MapPut("/{id}/file", TransferEndpoints.UploadAsync);
api.MapGet("/{id}/file", TransferEndpoints.DownloadAsync);

var replays = app.MapGroup("/api/replays").RequireRateLimiting("api");
replays.MapPut("/", ReplayExchange.PutAsync);
replays.MapGet("/", ReplayExchange.Get);

app.Run();

/// <summary>Exposed for WebApplicationFactory in the tests.</summary>
public partial class Program;
