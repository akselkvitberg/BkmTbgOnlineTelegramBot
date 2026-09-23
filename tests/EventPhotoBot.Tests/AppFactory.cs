using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using EventPhotoBot.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventPhotoBot.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Password = "hunter2";
    public const string SigningKey = "0123456789abcdef0123456789abcdef";
    public const string WebhookPath = "hook-abc";
    public const string WebhookSecret = "tg-secret";
    public const string JoinCode = "party2026";
    public const string DefaultRetentionSecret = "retention-secret";

    /// <summary>Set before the first client is created; null leaves RETENTION_SECRET unset.</summary>
    public string? RetentionSecret { get; init; } = DefaultRetentionSecret;

    public InMemoryObjectStore Objects { get; } = new();
    public FakeTelegramClient Telegram { get; } = new();

    /// <summary>
    /// Wraps Objects for the registered IObjectStore, for a test that needs one that
    /// fails in a specific way — e.g. simulating one failing GCS delete during a bulk
    /// delete. Assertions still read back through Objects itself, which the wrapper
    /// (when set) is expected to delegate to. Defaults to Objects unwrapped.
    /// </summary>
    public Func<InMemoryObjectStore, IObjectStore>? ObjectStoreOverride { get; init; }

    public StateStore Store => Services.GetRequiredService<StateStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("BUCKET_NAME", "test-bucket");
        builder.UseSetting("TELEGRAM_BOT_TOKEN", "test-token");
        builder.UseSetting("TELEGRAM_WEBHOOK_SECRET", WebhookSecret);
        builder.UseSetting("TELEGRAM_WEBHOOK_PATH", WebhookPath);
        builder.UseSetting("ADMIN_PASSWORD", Password);
        builder.UseSetting("COOKIE_SIGNING_KEY", SigningKey);
        builder.UseSetting("JOIN_CODE", JoinCode);
        if (RetentionSecret is not null) builder.UseSetting("RETENTION_SECRET", RetentionSecret);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(ObjectStoreOverride?.Invoke(Objects) ?? Objects);
            services.RemoveAll<ITelegramClient>();
            services.AddSingleton<ITelegramClient>(Telegram);
        });
    }

    /// <summary>A client carrying a valid session cookie, for everything behind the gate.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var cookie = SessionCookie.Issue(SigningKey, DateTimeOffset.UtcNow.AddDays(1));
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}={cookie}");
        return client;
    }

    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
