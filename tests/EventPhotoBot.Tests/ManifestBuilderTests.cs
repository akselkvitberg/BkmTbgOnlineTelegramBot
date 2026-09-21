using EventPhotoBot.State;
using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class ManifestBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_manifest_carries_the_join_url()
    {
        var manifest = ManifestBuilder.Build(
            new EventState(), generation: 1, Now, "https://t.me/bot?start=code");

        Assert.Equal("https://t.me/bot?start=code", manifest.Settings.JoinUrl);
    }

    [Fact]
    public void The_join_url_is_null_when_the_bot_username_is_unknown()
    {
        var manifest = ManifestBuilder.Build(new EventState(), generation: 1, Now);

        Assert.Null(manifest.Settings.JoinUrl);
    }

    [Fact]
    public void The_manifest_carries_the_ken_burns_setting()
    {
        var state = new EventState();
        Assert.True(ManifestBuilder.Build(state, 1, Now).Settings.KenBurns);

        state.Settings.KenBurns = false;

        Assert.False(ManifestBuilder.Build(state, 2, Now).Settings.KenBurns);
    }

    [Fact]
    public void The_manifest_carries_the_join_invite_setting()
    {
        var state = new EventState();
        Assert.True(ManifestBuilder.Build(state, 1, Now).Settings.ShowJoinInvite);

        state.Settings.ShowJoinInvite = false;

        Assert.False(ManifestBuilder.Build(state, 2, Now).Settings.ShowJoinInvite);
    }

    [Fact]
    public void Hiding_the_join_invite_leaves_the_join_url_intact()
    {
        // The setting is a screen-side choice, not a change to who may join: the
        // link keeps working for anyone who already has it, so the manifest still
        // carries it and only the slideshow's rendering changes.
        var state = new EventState { Settings = { ShowJoinInvite = false } };

        var manifest = ManifestBuilder.Build(state, 1, Now, "https://t.me/bot?start=code");

        Assert.Equal("https://t.me/bot?start=code", manifest.Settings.JoinUrl);
    }

    private static EventState StateWith(params (string Id, ImageStatus Status, PinKind Pin)[] images)
    {
        var state = new EventState();
        var n = 0;
        foreach (var (id, status, pin) in images)
        {
            state.Images[id] = new ImageRecord
            {
                Id = id,
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

        var manifest = ManifestBuilder.Build(state, 7, Now);

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

        Assert.Equal(2, ManifestBuilder.Build(state, 1, Now).PendingCount);
    }

    [Fact]
    public void Version_is_the_generation_it_was_given()
    {
        Assert.Equal(42, ManifestBuilder.Build(new EventState(), 42, Now).Version);
    }

    [Fact]
    public void Newest_first_ordering_puts_the_most_recent_first()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None),
            ("c", ImageStatus.Approved, PinKind.None));
        state.Settings.Order = SlideOrder.NewestFirst;

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.Equal(["c", "b", "a"], manifest.Images.Select(i => i.Id));
    }

    [Fact]
    public void Shuffle_is_stable_for_a_given_generation_and_changes_with_it()
    {
        var state = StateWith(Enumerable.Range(0, 20)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None)).ToArray());
        state.Settings.Order = SlideOrder.Shuffle;

        var first = ManifestBuilder.Build(state, 5, Now).Images.Select(i => i.Id).ToArray();
        var again = ManifestBuilder.Build(state, 5, Now).Images.Select(i => i.Id).ToArray();
        var later = ManifestBuilder.Build(state, 6, Now).Images.Select(i => i.Id).ToArray();

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
        state.Settings.Order = SlideOrder.NewestFirst;
        state.Settings.RecurringEvery = 3;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();

        // The menu appears after every third ordinary image, and is flagged.
        Assert.Equal("menu", ids[3]);
        Assert.Equal("menu", ids[7]);
        Assert.DoesNotContain("menu", ids.Take(3));
        Assert.True(ManifestBuilder.Build(state, 1, Now)
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
        state.Settings.Order = SlideOrder.NewestFirst;
        state.Settings.RecurringEvery = 2;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();
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
        state.Settings.RecurringEvery = 10;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();

        Assert.Contains("menu", ids);
        Assert.Contains("programme", ids);
    }

    [Fact]
    public void A_recurring_pin_is_not_also_listed_as_an_ordinary_image()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("menu", ImageStatus.Approved, PinKind.Recurring));
        state.Settings.RecurringEvery = 10;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();

        Assert.Single(ids, id => id == "menu");
    }

    [Fact]
    public void An_active_takeover_is_reported_with_its_expiry()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = Now.AddMinutes(15);

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.NotNull(manifest.Takeover);
        Assert.Equal("a", manifest.Takeover!.Id);
        Assert.Equal(Now.AddMinutes(15), manifest.Takeover.Until);
    }

    [Fact]
    public void A_takeover_with_no_expiry_runs_until_cleared()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = null;

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.NotNull(manifest.Takeover);
        Assert.Null(manifest.Takeover!.Until);
    }

    [Fact]
    public void An_expired_takeover_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = Now.AddMinutes(-1);

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_missing_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "gone";

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_hidden_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Hidden, PinKind.None));
        state.Settings.TakeoverImageId = "a";

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_pending_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Pending, PinKind.None));
        state.Settings.TakeoverImageId = "a";

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void The_playlist_is_still_built_while_a_takeover_is_active()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.Equal(2, manifest.Images.Count);
    }

    [Fact]
    public void An_empty_event_produces_an_empty_playlist_rather_than_throwing()
    {
        var manifest = ManifestBuilder.Build(new EventState(), 0, Now);

        Assert.Empty(manifest.Images);
        Assert.Null(manifest.Takeover);
        Assert.Equal(0, manifest.PendingCount);
    }

    [Fact]
    public void The_event_name_in_settings_is_carried_onto_the_settings_view()
    {
        var state = new EventState();
        state.Settings.EventName = "Summer Party";

        var manifest = ManifestBuilder.Build(state, 0, Now);

        Assert.Equal("Summer Party", manifest.Settings.EventName);
    }

    [Fact]
    public void An_event_whose_name_has_never_been_set_reports_an_empty_name()
    {
        // The screen's empty state hides the heading on an empty string, so a
        // freshly deployed event with nobody in admin yet shows no name rather
        // than the word "null".
        var manifest = ManifestBuilder.Build(new EventState(), 0, Now);

        Assert.Equal("", manifest.Settings.EventName);
    }
}
