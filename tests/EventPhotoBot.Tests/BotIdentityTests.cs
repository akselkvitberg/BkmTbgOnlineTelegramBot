using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class BotIdentityTests
{
    private static AppConfig Config() => new()
    {
        BucketName = "bucket", EventName = "Party", BotToken = "token",
        WebhookSecret = "secret", WebhookPath = "abc123", AdminPassword = "hunter2",
        CookieSigningKey = "0123456789abcdef0123456789abcdef", JoinCode = "party2026",
    };

    [Fact]
    public async Task Resolve_builds_the_deep_link_from_the_username_and_join_code()
    {
        var telegram = new FakeTelegramClient { Username = "eventphotobot" };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Equal("https://t.me/eventphotobot?start=party2026", identity.JoinUrl);
        Assert.Equal("eventphotobot", identity.Username);
    }

    [Fact]
    public async Task An_unavailable_username_leaves_the_join_link_null_without_throwing()
    {
        var telegram = new FakeTelegramClient { Username = null };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Null(identity.JoinUrl);
    }

    [Fact]
    public async Task A_throwing_getMe_leaves_the_join_link_null_without_throwing()
    {
        var telegram = new FakeTelegramClient { GetMeThrows = true };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Null(identity.JoinUrl);
    }
}
