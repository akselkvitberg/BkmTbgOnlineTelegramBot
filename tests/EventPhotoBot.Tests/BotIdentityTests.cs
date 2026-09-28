using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class BotIdentityTests
{
    private static BotIdentity Identity(FakeTelegramClient telegram) =>
        new(telegram, NullLogger<BotIdentity>.Instance);

    [Fact]
    public async Task Builds_the_deep_link_from_the_username_and_join_code()
    {
        var identity = Identity(new FakeTelegramClient { Username = "eventphotobot" });

        Assert.Equal("https://t.me/eventphotobot?start=party2026", await identity.GetJoinUrlAsync("party2026"));
        Assert.Equal("eventphotobot", await identity.GetUsernameAsync());
    }

    [Fact]
    public async Task Each_event_code_gets_its_own_link_from_one_lookup()
    {
        var telegram = new FakeTelegramClient { Username = "eventphotobot" };
        var identity = Identity(telegram);

        Assert.Equal("https://t.me/eventphotobot?start=a", await identity.GetJoinUrlAsync("a"));
        Assert.Equal("https://t.me/eventphotobot?start=b", await identity.GetJoinUrlAsync("b"));
        Assert.Equal(1, telegram.GetMeCalls);
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

        await identity.GetJoinUrlAsync("party2026");
        await identity.GetJoinUrlAsync("party2026");
        await identity.GetJoinUrlAsync("party2026");

        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_lookup()
    {
        var telegram = new FakeTelegramClient();
        var identity = Identity(telegram);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => identity.GetJoinUrlAsync("party2026"))));

        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task An_unavailable_username_leaves_the_join_link_null_without_throwing()
    {
        var identity = Identity(new FakeTelegramClient { Username = null });

        Assert.Null(await identity.GetJoinUrlAsync("party2026"));
    }

    [Fact]
    public async Task A_throwing_getMe_leaves_the_join_link_null_without_throwing()
    {
        var identity = Identity(new FakeTelegramClient { GetMeThrows = true });

        Assert.Null(await identity.GetJoinUrlAsync("party2026"));
    }

    [Fact]
    public async Task A_failure_is_not_retried_on_every_call()
    {
        // The manifest asks every two seconds; a Telegram outage must not become a
        // getMe per poll.
        var telegram = new FakeTelegramClient { GetMeThrows = true };
        var identity = Identity(telegram);

        await identity.GetJoinUrlAsync("party2026");
        telegram.GetMeThrows = false;
        var second = await identity.GetJoinUrlAsync("party2026");

        Assert.Null(second);
        Assert.Equal(1, telegram.GetMeCalls);
    }

    [Fact]
    public async Task A_hanging_getMe_gives_up_instead_of_holding_the_request()
    {
        var identity = Identity(new FakeTelegramClient { GetMeHangs = true });

        var lookup = identity.GetJoinUrlAsync("party2026");
        var finished = await Task.WhenAny(lookup, Task.Delay(BotIdentity.LookupTimeout * 3));

        Assert.Same(lookup, finished);
        Assert.Null(await lookup);
    }
}
