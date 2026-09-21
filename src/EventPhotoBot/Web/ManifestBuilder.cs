using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record ManifestImage(
    string Id, int Width, int Height, string? Caption, string? SenderName, bool Recurring);

public sealed record TakeoverView(string Id, DateTimeOffset? Until);

public sealed record SettingsView(
    int SlideSeconds, int TransitionMs, bool NewestFirstBoost, string Order, string EventName,
    string? JoinUrl, bool KenBurns);

public sealed record Manifest(
    long Version,
    IReadOnlyList<ManifestImage> Images,
    TakeoverView? Takeover,
    SettingsView Settings,
    int PendingCount);

/// <summary>
/// Pure function from state to what the screen should show. No I/O, no clock of
/// its own — the caller supplies 'now' so the behaviour is testable.
/// </summary>
public static class ManifestBuilder
{
    public static Manifest Build(
        EventState state, long generation, DateTimeOffset now, string eventName = "",
        string? joinUrl = null)
    {
        var settings = state.Settings;

        var approved = state.Images.Values
            .Where(i => i.Status == ImageStatus.Approved)
            .ToList();

        var recurring = approved
            .Where(i => i.Pin == PinKind.Recurring)
            .OrderBy(i => i.SortKey, StringComparer.Ordinal)
            .ToList();

        var ordinary = approved.Where(i => i.Pin != PinKind.Recurring).ToList();
        ordinary = settings.Order == SlideOrder.NewestFirst
            ? [.. ordinary.OrderByDescending(i => i.SortKey, StringComparer.Ordinal)]
            : Shuffle(ordinary, generation);

        var playlist = Interleave(ordinary, recurring, Math.Max(1, settings.RecurringEvery));

        return new Manifest(
            Version: generation,
            Images: [.. playlist.Select(ToManifestImage)],
            Takeover: ActiveTakeover(state, now),
            Settings: new SettingsView(
                settings.SlideSeconds, settings.TransitionMs, settings.NewestFirstBoost,
                settings.Order == SlideOrder.NewestFirst ? "newest-first" : "shuffle", eventName,
                joinUrl, settings.KenBurns),
            PendingCount: state.Images.Values.Count(i => i.Status == ImageStatus.Pending));
    }

    private static ManifestImage ToManifestImage(ImageRecord i) =>
        new(i.Id, i.Width, i.Height, i.Caption, i.SenderName, i.Pin == PinKind.Recurring);

    /// <summary>
    /// Seeded with the generation so the order is stable across polls and only
    /// changes when the state does. An unseeded shuffle would reorder the screen
    /// on every poll that misses the ETag.
    /// </summary>
    private static List<ImageRecord> Shuffle(List<ImageRecord> images, long generation)
    {
        var ordered = images.OrderBy(i => i.SortKey, StringComparer.Ordinal).ToList();
        var random = new Random(unchecked((int)generation));
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }
        return ordered;
    }

    /// <summary>
    /// Drops one recurring image into the playlist after every 'every' ordinary
    /// images, rotating when several hold the pin.
    /// </summary>
    private static List<ImageRecord> Interleave(
        List<ImageRecord> ordinary, List<ImageRecord> recurring, int every)
    {
        if (recurring.Count == 0) return ordinary;
        if (ordinary.Count == 0) return recurring;

        var result = new List<ImageRecord>(ordinary.Count + ordinary.Count / every + 1);
        var next = 0;
        for (var i = 0; i < ordinary.Count; i++)
        {
            result.Add(ordinary[i]);
            if ((i + 1) % every == 0)
            {
                result.Add(recurring[next % recurring.Count]);
                next++;
            }
        }
        // ordinary.Count < every means the loop above never reached a multiple of
        // 'every', so no recurring image was scheduled at all - the pin(s) would
        // otherwise never appear until enough ordinary images accumulate. Appending
        // every recurring image once (not just recurring[0]) is what keeps a second
        // pinned image from being silently dropped at small image counts.
        if (next == 0) result.AddRange(recurring);
        return result;
    }

    private static TakeoverView? ActiveTakeover(EventState state, DateTimeOffset now)
    {
        var settings = state.Settings;
        if (settings.TakeoverImageId is not { } id) return null;
        if (!state.Images.TryGetValue(id, out var image) || image.Status != ImageStatus.Approved)
            return null;
        if (settings.TakeoverUntil is { } until && until <= now) return null;
        return new TakeoverView(id, settings.TakeoverUntil);
    }
}
