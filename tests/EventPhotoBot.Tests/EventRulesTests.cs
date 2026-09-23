using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class EventRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static Event At(DateTimeOffset? opens = null, DateTimeOffset? closes = null, DateTimeOffset? closedAt = null) =>
        new() { Id = "x", JoinCode = "c", OpensAt = opens, ClosesAt = closes, ClosedAt = closedAt };

    [Fact] public void No_times_is_open() => Assert.Equal(EventPhase.Open, At().PhaseAt(Now));
    [Fact] public void Before_opens_at_is_scheduled() => Assert.Equal(EventPhase.Scheduled, At(opens: Now.AddHours(1)).PhaseAt(Now));
    [Fact] public void At_opens_at_is_open() => Assert.True(At(opens: Now).IsOpen(Now));
    [Fact] public void At_closes_at_is_closed() => Assert.Equal(EventPhase.Closed, At(closes: Now).PhaseAt(Now));
    [Fact] public void A_manual_close_wins_over_the_schedule() =>
        Assert.Equal(EventPhase.Closed, At(opens: Now.AddHours(1), closedAt: Now.AddHours(-1)).PhaseAt(Now));

    [Fact]
    public void Generated_codes_are_unique_among_the_events()
    {
        var state = new EventState();
        StateMigration.Migrate(state, "party2026", Now);

        var code = EventRules.UniqueJoinCode(state);

        Assert.NotEqual("party2026", code);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", code);
    }
}
