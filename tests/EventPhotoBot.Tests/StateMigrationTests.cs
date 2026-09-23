using System.Text.Json;
using System.Text.RegularExpressions;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class StateMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static EventState Load(string json) =>
        JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

    [Fact]
    public void A_fresh_state_gets_one_default_event_with_the_seeded_code()
    {
        var state = new EventState();

        Assert.True(StateMigration.Migrate(state, "party2026", Now));

        var ev = Assert.Single(state.Events);
        Assert.True(ev.IsDefault);
        Assert.Equal(StateMigration.DefaultEventId, ev.Id);
        Assert.Equal(StateMigration.DefaultEventName, ev.Name);
        Assert.Equal("party2026", ev.JoinCode);
        Assert.Null(ev.Retention.MaxAgeDays);
    }

    [Fact]
    public void Without_a_seed_the_default_event_gets_a_generated_deep_link_safe_code()
    {
        var state = new EventState();

        StateMigration.Migrate(state, null, Now);

        Assert.Matches(new Regex("^[A-Za-z0-9_-]{22}$"), state.Default().JoinCode);
    }

    [Fact]
    public void A_pre_events_file_moves_every_value_to_the_default_event()
    {
        const string json = """
            {"images":{"01A":{"id":"01A","sha256":"x","sortKey":"01A","originalExtension":"jpg","status":"approved"}},
             "settings":{"eventName":"Sommerfest","slideSeconds":12,"layout":"mosaic","showJoinInvite":false,
                         "takeoverImageId":"01A","takeoverUntil":"2026-09-23T13:00:00+00:00",
                         "senders":[{"id":1,"name":"Ada","status":"known"},
                                    {"id":2,"name":"Bo","status":"autoApprove"},
                                    {"id":3,"name":"Cy","status":"banned"}],
                         "groups":[{"id":-10,"title":"On","listening":true},
                                   {"id":-11,"title":"Off","listening":false}]}}
            """;
        var state = Load(json);

        Assert.True(StateMigration.Migrate(state, "party2026", Now));

        var ev = state.Default();
        Assert.Equal("Sommerfest", ev.Name);
        Assert.Equal(12, ev.Settings.SlideSeconds);
        Assert.Equal(SlideLayout.Mosaic, ev.Settings.Layout);
        Assert.False(ev.Settings.ShowJoinInvite);
        // Review focus 1: a takeover running across the upgrade keeps the screen.
        Assert.Equal("01A", ev.Settings.TakeoverImageId);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero), ev.Settings.TakeoverUntil);

        Assert.Equal(ev.Id, state.Images["01A"].EventId);

        var ada = state.Senders.Single(s => s.Id == 1);
        Assert.False(ada.Banned);
        Assert.False(ada.MembershipIn(ev.Id)!.AutoApprove);
        Assert.Equal(ev.Id, ada.CurrentEventId);
        Assert.True(state.Senders.Single(s => s.Id == 2).MembershipIn(ev.Id)!.AutoApprove);
        var cy = state.Senders.Single(s => s.Id == 3);
        Assert.True(cy.Banned);
        Assert.NotNull(cy.MembershipIn(ev.Id));   // an unban restores it

        Assert.Equal(ev.Id, state.Groups.Single(g => g.Id == -10).EventId);
        Assert.Null(state.Groups.Single(g => g.Id == -11).EventId);

        Assert.Null(state.Legacy);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(state, StateJson.Options));
        Assert.False(written.RootElement.TryGetProperty("settings", out _));
    }

    [Fact]
    public void A_file_from_before_groups_migrates_with_no_groups()
    {
        var state = Load("""{"images":{},"settings":{"senders":[{"id":42,"name":"Ada","status":"known"}]}}""");

        StateMigration.Migrate(state, "party2026", Now);

        Assert.Empty(state.Groups);
        Assert.Single(state.Senders);
    }

    [Fact]
    public void An_empty_legacy_name_becomes_daglig()
    {
        var state = Load("""{"images":{},"settings":{"eventName":""}}""");

        StateMigration.Migrate(state, "party2026", Now);

        Assert.Equal("Daglig", state.Default().Name);
    }

    [Fact]
    public void Migrating_a_migrated_state_changes_nothing()
    {
        var state = new EventState();
        StateMigration.Migrate(state, "party2026", Now);
        var before = JsonSerializer.Serialize(state, StateJson.Options);

        Assert.False(StateMigration.Migrate(state, "other", Now.AddDays(1)));
        Assert.Equal(before, JsonSerializer.Serialize(state, StateJson.Options));
    }
}
