using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class RoutingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (EventState State, Sender Sender) Setup(string? current, bool closed, bool dailyMember, bool weddingMember = true)
    {
        var state = TestState.New();
        state.AddEvent("bryllup", closed: closed);
        var sender = new Sender { Id = 1, CurrentEventId = current };
        if (weddingMember) sender.Memberships.Add(new Membership { EventId = "bryllup" });
        if (dailyMember) sender.Memberships.Add(new Membership { EventId = "daglig" });
        return (state, sender);
    }

    [Fact]
    public void The_current_event_while_it_is_open()
    {
        var (state, sender) = Setup("bryllup", closed: false, dailyMember: true);
        Assert.Equal("bryllup", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void The_default_event_once_the_current_one_closes_for_a_member_of_it()
    {
        var (state, sender) = Setup("bryllup", closed: true, dailyMember: true);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void Nowhere_once_the_current_one_closes_for_someone_who_never_joined_the_default()
    {
        var (state, sender) = Setup("bryllup", closed: true, dailyMember: false);
        Assert.Null(Routing.ResolvePrivateTarget(state, sender, Now));
    }

    [Fact]
    public void The_default_event_when_the_current_one_was_deleted()
    {
        // Review focus 3.
        var (state, sender) = Setup("gone", closed: false, dailyMember: true);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void Not_an_event_the_sender_is_no_longer_a_member_of()
    {
        var (state, sender) = Setup("bryllup", closed: false, dailyMember: true, weddingMember: false);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }
}
