using System.Security.Cryptography;
using System.Text;
using EventPhotoBot;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Web;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// Cloud Run supplies PORT and expects the container to listen on it; the port
// is not hardcoded here or in the image. Defaulting to 8080 when PORT is
// unset keeps `dotnet run` and the test host working unchanged.
var port = builder.Configuration["PORT"] ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var config = AppConfig.Load(builder.Configuration);
builder.Services.AddSingleton(config);

builder.Services.AddSingleton<IObjectStore>(sp => new GcsObjectStore(
    config.BucketName,
    StorageClient.Create(),
    sp.GetRequiredService<ILogger<GcsObjectStore>>()));

builder.Services.AddSingleton<StateStore>();
builder.Services.AddHttpClient<ITelegramClient, TelegramClient>();
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddLoginRateLimiter();

// Cloud Run terminates TLS and proxies every request, so Connection.RemoteIpAddress
// is the proxy, not the caller. Without this, the login rate limiter's "per IP"
// partition collapses into a single shared bucket for the whole service.
// ForwardLimit = 1 trusts exactly one hop (Cloud Run's own front end); the known
// networks/proxies lists are cleared because Cloud Run's front end has no fixed,
// listable address to check against.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

config.LogLoaded(app.Logger);

// Load state once, at startup. This is the only read of state.json.
await app.Services.GetRequiredService<StateStore>().LoadAsync();

app.UseForwardedHeaders();

app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost($"/tg/{config.WebhookPath}",
    async (HttpContext http, TgUpdate update, UpdateHandler handler, CancellationToken ct) =>
    {
        if (!SecretTokenMatches(config.WebhookSecret,
                http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"]))
            return Results.Unauthorized();

        try
        {
            await handler.HandleAsync(update, ct);
        }
        catch (Exception e)
        {
            app.Logger.LogError(e, "Unhandled error processing update {UpdateId}.", update.UpdateId);
        }

        // Always 200 once the update is parsed. Telegram's retry would resend the
        // whole update and risk duplicates; the sender already got an apology.
        return Results.Ok();
    });

app.MapAuth(config);
app.UseSessionGate(config);

app.UseStaticFiles();
app.MapApi();
app.MapImages();

app.MapGet("/show", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "show.html"), "text/html"));
app.MapGet("/admin/queue", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "queue.html"), "text/html"));
app.MapGet("/admin/images", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "images.html"), "text/html"));
app.MapGet("/admin/settings", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "settings.html"), "text/html"));
app.MapGet("/", () => Results.Redirect("/show"));

app.Run();

/// <summary>
/// Constant-time over the UTF-8 bytes, the one endpoint strangers can reach.
/// Hashes both sides before comparing, the same way SessionCookie.PasswordMatches
/// does it: CryptographicOperations.FixedTimeEquals alone still leaks length through
/// its own argument check unless both inputs are already the same size, and hashing
/// first fixes that at 32 bytes regardless of what was supplied — a missing header
/// (null) hashes and compares exactly like a present-but-wrong one.
/// </summary>
static bool SecretTokenMatches(string expected, string? supplied)
{
    Span<byte> hashA = stackalloc byte[32];
    Span<byte> hashB = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(expected), hashA);
    SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""), hashB);
    return CryptographicOperations.FixedTimeEquals(hashA, hashB);
}

public partial class Program { }
