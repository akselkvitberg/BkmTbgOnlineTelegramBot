using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class BotIdentityTests
{
    private static AppConfig Config() => new()
    {
        BucketName = "bucket", BotToken = "token",
        WebhookSecret = "secret", WebhookPath = "abc123", AdminPassword = "hunter2",
        CookieSigningKey = "0123456789abcdef0123456789abcdef", JoinCode = "party2026",
    };

    private static BotIdentity Identity(FakeTelegramClient telegram) =>
        new(Config(), telegram, NullLogger<BotIdentity>.Instance);

    [Fact]
    public async Task Builds_the_deep_link_from_the_username_and_join_code()
    {
        var identity = Identity(new FakeTelegramClient { Username = "eventphotobot" });

        Assert.Equal("https://t.me/eventphotobot?start=party2026", await identity.GetJoinUrlAsync());
        Assert.Equal("eventphotobot", await identity.GetUsernameAsync());
    }

    [Fact]
    public void Constructing_it_does_not_ask_telegram()
    {
        var telegram = new FakeTelegramClient();

        _ = Identity(telegram);

        Assert.Equal(0, telegram.GetMeCalls);
    }

    [Fact]
    public async Task A_known_username_is_asked_for_once_and_then_cached()
    {
        var telegram = new FakeTelegramClient();
        var identity = Identity(telegram);

        await identity.GetJoinUrlAsync();
        await identity.GetJoinUrlAsync();
        await identity.GetJoinUrlAsync();

        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_lookup()
    {
        var telegram = new FakeTelegramClient();
        var identity = Identity(telegram);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => identity.GetJoinUrlAsync())));

        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task An_unavailable_username_leaves_the_join_link_null_without_throwing()
    {
        var identity = Identity(new FakeTelegramClient { Username = null });

        Assert.Null(await identity.GetJoinUrlAsync());
    }

    [Fact]
    public async Task A_throwing_getMe_leaves_the_join_link_null_without_throwing()
    {
        var identity = Identity(new FakeTelegramClient { GetMeThrows = true });

        Assert.Null(await identity.GetJoinUrlAsync());
    }

    [Fact]
    public async Task A_failure_is_not_retried_on_every_call()
    {
        // The manifest asks every two seconds; a Telegram outage must not become a
        // getMe per poll.
        var telegram = new FakeTelegramClient { GetMeThrows = true };
        var identity = Identity(telegram);

        await identity.GetJoinUrlAsync();
        telegram.GetMeThrows = false;
        var second = await identity.GetJoinUrlAsync();

        Assert.Null(second);
        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task A_hanging_getMe_gives_up_instead_of_holding_the_request()
    {
        var identity = Identity(new FakeTelegramClient { GetMeHangs = true });

        var lookup = identity.GetJoinUrlAsync();
        var finished = await Task.WhenAny(lookup, Task.Delay(BotIdentity.LookupTimeout * 3));

        Assert.Same(lookup, finished);
        Assert.Null(await lookup);
    }
}
