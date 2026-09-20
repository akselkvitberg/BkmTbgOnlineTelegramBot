using EventPhotoBot;
using EventPhotoBot.State;
using EventPhotoBot.Web;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var config = AppConfig.Load(builder.Configuration);
builder.Services.AddSingleton(config);

builder.Services.AddSingleton<IObjectStore>(sp => new GcsObjectStore(
    config.BucketName,
    StorageClient.Create(),
    sp.GetRequiredService<ILogger<GcsObjectStore>>()));

builder.Services.AddSingleton<StateStore>();
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

public partial class Program { }
