using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;
using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class ManifestBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 20, 0, 0, TimeSpan.Zero);

    private static Manifest Build(EventState state, long generation, string? joinUrl = null) =>
        ManifestBuilder.Build(state, state.Default(), generation, Now, joinUrl);

    [Fact]
    public void The_manifest_carries_the_join_url()
    {
        var manifest = Build(TestState.New(), generation: 1, "https://t.me/bot?start=code");

        Assert.Equal("https://t.me/bot?start=code", manifest.Settings.JoinUrl);
    }

    [Fact]
    public void The_join_url_is_null_when_the_bot_username_is_unknown()
    {
        var manifest = Build(TestState.New(), generation: 1);

        Assert.Null(manifest.Settings.JoinUrl);
    }

    [Fact]
    public void The_manifest_carries_the_ken_burns_setting()
    {
        var state = TestState.New();
        Assert.True(Build(state, 1).Settings.KenBurns);

        state.Default().Settings.KenBurns = false;

        Assert.False(Build(state, 2).Settings.KenBurns);
    }

    [Fact]
    public void The_manifest_carries_the_join_invite_setting()
    {
        var state = TestState.New();
        Assert.True(Build(state, 1).Settings.ShowJoinInvite);

        state.Default().Settings.ShowJoinInvite = false;

        Assert.False(Build(state, 2).Settings.ShowJoinInvite);
    }

    [Fact]
    public void The_manifest_carries_the_event_name_setting()
    {
        var state = TestState.New();
        Assert.True(Build(state, 1).Settings.ShowEventName);

        state.Default().Settings.ShowEventName = false;

        Assert.False(Build(state, 2).Settings.ShowEventName);
    }

    [Fact]
    public void The_manifest_carries_the_layout_as_a_lowercase_name()
    {
        var state = TestState.New();

        // The screen it was written for, and what an event that has never touched
        // the setting gets.
        Assert.Equal("single", Build(state, 1).Settings.Layout);

        state.Default().Settings.Layout = SlideLayout.Filmstrip;

        Assert.Equal("filmstrip", Build(state, 2).Settings.Layout);
    }

    [Theory]
    [InlineData(SlideLayout.Single, "single")]
    [InlineData(SlideLayout.Mosaic, "mosaic")]
    [InlineData(SlideLayout.Polaroid, "polaroid")]
    [InlineData(SlideLayout.Filmstrip, "filmstrip")]
    [InlineData(SlideLayout.Collage, "collage")]
    [InlineData(SlideLayout.Split, "split")]
    public void Every_layout_has_a_name_the_screen_knows(SlideLayout layout, string expected)
    {
        // show.js looks the name up in its own layout table and falls back to the
        // single layout on anything it does not recognise, so a mismatch here would
        // not fail loudly - it would quietly ignore the organiser's choice. The
        // spellings are asserted one by one for that reason.
        var state = TestState.New();
        state.Default().Settings.Layout = layout;

        Assert.Equal(expected, Build(state, 1).Settings.Layout);
    }

    [Fact]
    public void Hiding_the_join_invite_leaves_the_join_url_intact()
    {
        // The setting is a screen-side choice, not a change to who may join: the
        // link keeps working for anyone who already has it, so the manifest still
        // carries it and only the slideshow's rendering changes.
        var state = TestState.New();
        state.Default().Settings.ShowJoinInvite = false;

        var manifest = Build(state, 1, "https://t.me/bot?start=code");

        Assert.Equal("https://t.me/bot?start=code", manifest.Settings.JoinUrl);
    }

    private static EventState StateWith(params (string Id, ImageStatus Status, PinKind Pin)[] images)
    {
        var state = TestState.New();
        var n = 0;
        foreach (var (id, status, pin) in images)
        {
            state.Images[id] = new ImageRecord
            {
                Id = id,
                EventId = StateMigration.DefaultEventId,
                Status = status,
                Pin = pin,
                Sha256 = id,
                Width = 100,
                Height = 100,
                SortKey = $"{n:D4}",
                ReceivedAt = Now.AddMinutes(n),
                OriginalExtension = "jpg",
            };
            n++;
        }
        return state;
    }

    [Fact]
    public void Only_approved_images_reach_the_playlist()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Pending, PinKind.None),
            ("c", ImageStatus.Hidden, PinKind.None),
            ("d", ImageStatus.Rejected, PinKind.None));

        var manifest = Build(state, 7);

        Assert.Equal(["a"], manifest.Images.Select(i => i.Id));
    }

    [Fact]
    public void Pending_count_counts_only_pending_images()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Pending, PinKind.None),
            ("c", ImageStatus.Pending, PinKind.None),
            ("d", ImageStatus.Rejected, PinKind.None));

        Assert.Equal(2, Build(state, 1).PendingCount);
    }

    [Fact]
    public void Version_is_the_generation_it_was_given()
    {
        Assert.Equal(42, Build(TestState.New(), 42).Version);
    }

    [Fact]
    public void Newest_first_ordering_puts_the_most_recent_first()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None),
            ("c", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.Order = SlideOrder.NewestFirst;

        var manifest = Build(state, 1);

        Assert.Equal(["c", "b", "a"], manifest.Images.Select(i => i.Id));
    }

    [Fact]
    public void Shuffle_is_stable_for_a_given_generation_and_changes_with_it()
    {
        var state = StateWith(Enumerable.Range(0, 20)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None)).ToArray());
        state.Default().Settings.Order = SlideOrder.Shuffle;

        var first = Build(state, 5).Images.Select(i => i.Id).ToArray();
        var again = Build(state, 5).Images.Select(i => i.Id).ToArray();
        var later = Build(state, 6).Images.Select(i => i.Id).ToArray();

        Assert.Equal(first, again);
        Assert.NotEqual(first, later);
        Assert.Equal(first.Order(), later.Order());
    }

    [Fact]
    public void Recurring_pins_are_interleaved_at_the_configured_interval()
    {
        var images = Enumerable.Range(0, 9)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None))
            .Append(("menu", ImageStatus.Approved, PinKind.Recurring))
            .ToArray();
        var state = StateWith(images);
        state.Default().Settings.Order = SlideOrder.NewestFirst;
        state.Default().Settings.RecurringEvery = 3;

        var ids = Build(state, 1).Images.Select(i => i.Id).ToArray();

        // The menu appears after every third ordinary image, and is flagged.
        Assert.Equal("menu", ids[3]);
        Assert.Equal("menu", ids[7]);
        Assert.DoesNotContain("menu", ids.Take(3));
        Assert.True(Build(state, 1)
            .Images.First(i => i.Id == "menu").Recurring);
    }

    [Fact]
    public void Several_recurring_pins_rotate_through_the_slot()
    {
        var images = Enumerable.Range(0, 6)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None))
            .Concat([("menu", ImageStatus.Approved, PinKind.Recurring),
                     ("programme", ImageStatus.Approved, PinKind.Recurring)])
            .ToArray();
        var state = StateWith(images);
        state.Default().Settings.Order = SlideOrder.NewestFirst;
        state.Default().Settings.RecurringEvery = 2;

        var ids = Build(state, 1).Images.Select(i => i.Id).ToArray();
        var pinned = ids.Where(id => id is "menu" or "programme").ToArray();

        Assert.True(pinned.Length >= 2);
        Assert.NotEqual(pinned[0], pinned[1]);
    }

    [Fact]
    public void Every_recurring_pin_still_shows_when_there_are_fewer_ordinary_images_than_the_interval()
    {
        // ordinary.Count (2) < RecurringEvery (10): the main interleave loop never
        // reaches a multiple of 'every', so both pins depend entirely on the
        // fallback branch. Only appending recurring[0] there silently dropped the
        // second pinned image until enough ordinary images accumulated.
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None),
            ("menu", ImageStatus.Approved, PinKind.Recurring),
            ("programme", ImageStatus.Approved, PinKind.Recurring));
        state.Default().Settings.RecurringEvery = 10;

        var ids = Build(state, 1).Images.Select(i => i.Id).ToArray();

        Assert.Contains("menu", ids);
        Assert.Contains("programme", ids);
    }

    [Fact]
    public void A_recurring_pin_is_not_also_listed_as_an_ordinary_image()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("menu", ImageStatus.Approved, PinKind.Recurring));
        state.Default().Settings.RecurringEvery = 10;

        var ids = Build(state, 1).Images.Select(i => i.Id).ToArray();

        Assert.Single(ids, id => id == "menu");
    }

    [Fact]
    public void An_active_takeover_is_reported_with_its_expiry()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";
        state.Default().Settings.TakeoverUntil = Now.AddMinutes(15);

        var manifest = Build(state, 1);

        Assert.NotNull(manifest.Takeover);
        Assert.Equal("a", manifest.Takeover!.Id);
        Assert.Equal(Now.AddMinutes(15), manifest.Takeover.Until);
    }

    [Fact]
    public void A_takeover_with_no_expiry_runs_until_cleared()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";
        state.Default().Settings.TakeoverUntil = null;

        var manifest = Build(state, 1);

        Assert.NotNull(manifest.Takeover);
        Assert.Null(manifest.Takeover!.Until);
    }

    [Fact]
    public void An_expired_takeover_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";
        state.Default().Settings.TakeoverUntil = Now.AddMinutes(-1);

        Assert.Null(Build(state, 1).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_missing_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "gone";

        Assert.Null(Build(state, 1).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_hidden_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Hidden, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";

        Assert.Null(Build(state, 1).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_pending_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Pending, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";

        Assert.Null(Build(state, 1).Takeover);
    }

    [Fact]
    public void The_playlist_is_still_built_while_a_takeover_is_active()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "a";

        var manifest = Build(state, 1);

        Assert.Equal(2, manifest.Images.Count);
    }

    [Fact]
    public void An_empty_event_produces_an_empty_playlist_rather_than_throwing()
    {
        var manifest = Build(TestState.New(), 0);

        Assert.Empty(manifest.Images);
        Assert.Null(manifest.Takeover);
        Assert.Equal(0, manifest.PendingCount);
    }

    [Fact]
    public void The_event_name_in_settings_is_carried_onto_the_settings_view()
    {
        var state = TestState.New();
        state.Default().Name = "Summer Party";

        var manifest = Build(state, 0);

        Assert.Equal("Summer Party", manifest.Settings.EventName);
    }

    [Fact]
    public void An_empty_event_name_is_passed_through_to_the_screen()
    {
        // The screen's empty state hides the heading on an empty string, so an
        // event whose name was cleared shows no name rather than the word "null".
        var state = TestState.New();
        state.Default().Name = "";

        var manifest = Build(state, 0);

        Assert.Equal("", manifest.Settings.EventName);
    }

    [Fact]
    public void Another_events_images_and_pending_count_stay_out()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None), ("p", ImageStatus.Pending, PinKind.None));
        state.AddEvent("other");
        state.Images["o"] = new ImageRecord
        {
            Id = "o", EventId = "other", Status = ImageStatus.Approved, Sha256 = "o", SortKey = "9999", OriginalExtension = "jpg",
        };

        var manifest = Build(state, 1);

        Assert.Equal(["a"], manifest.Images.Select(i => i.Id));
        Assert.Equal(1, manifest.PendingCount);
    }

    [Fact]
    public void The_pending_total_counts_every_open_event_and_skips_closed_ones()
    {
        var state = StateWith(("p1", ImageStatus.Pending, PinKind.None));
        state.AddEvent("open");
        state.AddEvent("shut", closed: true);
        foreach (var (id, ev) in new[] { ("p2", "open"), ("p3", "shut") })
            state.Images[id] = new ImageRecord
            {
                Id = id, EventId = ev, Status = ImageStatus.Pending, Sha256 = id, SortKey = id, OriginalExtension = "jpg",
            };

        Assert.Equal(2, Build(state, 1).PendingTotal);
    }

    [Fact]
    public void A_closed_event_hides_the_invite_whatever_the_setting_says()
    {
        var state = TestState.New();
        var ev = state.AddEvent("shut", closed: true);

        var manifest = ManifestBuilder.Build(state, ev, 1, Now, "https://t.me/bot?start=x");

        Assert.False(manifest.Settings.ShowJoinInvite);
        Assert.Null(manifest.Settings.JoinUrl);
    }
}
