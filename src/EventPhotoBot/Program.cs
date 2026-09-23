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
builder.Services.AddSingleton(new StateSeed(config.JoinCode));

// A flag that swaps storage for the local disk and silences Telegram must never
// reach Cloud Run by accident; there the environment is Production.
if (config.LocalDev && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("LOCAL_DEV is only allowed in the Development environment.");

if (config.LocalDev)
{
    builder.Services.AddSingleton<IObjectStore>(
        new FileSystemObjectStore(Path.Combine(builder.Environment.ContentRootPath, config.StorageDir!)));
}
else
{
    builder.Services.AddSingleton<IObjectStore>(sp => new GcsObjectStore(
        config.BucketName,
        StorageClient.Create(),
        sp.GetRequiredService<ILogger<GcsObjectStore>>()));
}

builder.Services.AddSingleton<StateStore>();
// HttpClientFactory's logging handlers log the request URI at Information, and
// Telegram puts the bot token in the URL PATH (https://api.telegram.org/bot<token>/...),
// which the factory's redaction only strips from the query string. Left at the default
// level, every getFile/download/sendMessage call would write the live bot token to Cloud
// Logging — a secret that outlives `terraform destroy`, since logs aren't a Terraform
// resource. appsettings.json sets "System.Net.Http.HttpClient": "Warning" to silence
// exactly those Information-level request/response lines; this is not noise suppression,
// it's the outbound half of the same care "Microsoft.AspNetCore": "Warning" already gives
// the inbound side. An explicit 60s timeout also keeps a slow Telegram call from eating
// most of Cloud Run's 120s request timeout, leaving room for decode plus object writes.
if (config.LocalDev)
{
    builder.Services.AddSingleton<OfflineTelegramClient>();
    builder.Services.AddSingleton<ITelegramClient>(sp => sp.GetRequiredService<OfflineTelegramClient>());
}
else
{
    builder.Services.AddHttpClient<ITelegramClient, TelegramClient>(
        client => client.Timeout = TimeSpan.FromSeconds(60));
}
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddSingleton<BotIdentity>();
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

// Load state once, at startup — the only read of state.json — and persist any migration.
await app.Services.GetRequiredService<StateStore>().InitializeAsync();

// Vanity, not correctness: a failure here omits the join QR and nothing else,
// so unlike the state load above it must never stop the revision coming up.
await app.Services.GetRequiredService<BotIdentity>().ResolveAsync(
    app.Services.GetRequiredService<ITelegramClient>(),
    app.Services.GetRequiredService<ILogger<BotIdentity>>());

app.UseForwardedHeaders();

app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost($"/tg/{config.WebhookPath}",
    async (HttpContext http, TgUpdate update, UpdateHandler handler, CancellationToken ct) =>
    {
        if (!SecretComparison.Matches(config.WebhookSecret,
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
app.MapEvents();
app.MapImages();
app.MapJoinQr();
if (config.LocalDev) app.MapDev();

app.MapGet("/show", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "show.html"), "text/html"));
app.MapGet("/upload", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "upload.html"), "text/html"));
app.MapGet("/admin/queue", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "queue.html"), "text/html"));
app.MapGet("/admin/images", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "images.html"), "text/html"));
app.MapGet("/admin/telegram", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "telegram.html"), "text/html"));
app.MapGet("/admin/settings", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "settings.html"), "text/html"));
app.MapGet("/", () => Results.Redirect("/show"));

app.Run();

public partial class Program { }
