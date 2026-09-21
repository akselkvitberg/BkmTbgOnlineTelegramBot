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

    public InMemoryObjectStore Objects { get; } = new();
    public FakeTelegramClient Telegram { get; } = new();

    public StateStore Store => Services.GetRequiredService<StateStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("BUCKET_NAME", "test-bucket");
        builder.UseSetting("EVENT_NAME", "Test Event");
        builder.UseSetting("TELEGRAM_BOT_TOKEN", "test-token");
        builder.UseSetting("TELEGRAM_WEBHOOK_SECRET", WebhookSecret);
        builder.UseSetting("TELEGRAM_WEBHOOK_PATH", WebhookPath);
        builder.UseSetting("ADMIN_PASSWORD", Password);
        builder.UseSetting("COOKIE_SIGNING_KEY", SigningKey);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(Objects);
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
