namespace EventPhotoBot.State;

public static class RetentionSweep
{
    /// <summary>
    /// The photos retention would delete from one event now. Kept: everything received
    /// within MaxAgeDays, the KeepNewest newest approved (so the screen always has a
    /// pool), pinned photos, and the one holding the screen.
    /// </summary>
    public static IReadOnlyList<ImageRecord> Select(EventState state, Event ev, DateTimeOffset now)
    {
        if (ev.Retention.MaxAgeDays is not { } days) return [];
        var cutoff = now.AddDays(-days);
        var images = state.Images.Values.Where(i => i.EventId == ev.Id).ToList();

        var keep = images
            .Where(i => i.Status == ImageStatus.Approved)
            .OrderByDescending(i => i.ReceivedAt)
            .ThenByDescending(i => i.SortKey, StringComparer.Ordinal)
            .Take(ev.Retention.KeepNewest)
            .Select(i => i.Id)
            .ToHashSet();
        if (ev.Settings.TakeoverImageId is { } held) keep.Add(held);

        return [.. images.Where(i => i.ReceivedAt < cutoff && i.Pin != PinKind.Recurring && !keep.Contains(i.Id))];
    }

    /// <summary>
    /// Every event with retention on, or just <paramref name="onlyEventId"/>. One state
    /// write for the lot, then the objects — the same order as every other delete.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> RunAsync(StateStore store, IObjectStore objects,
        ILogger logger, string? onlyEventId, DateTimeOffset now, CancellationToken ct)
    {
        bool InScope(Event e) => onlyEventId is null || e.Id == onlyEventId;

        // MutateAsync always writes; a nightly no-op must not bump every screen's ETag.
        if (!store.Snapshot.Events.Where(InScope).Any(e => Select(store.Snapshot, e, now).Count > 0))
            return new Dictionary<string, int>();

        var removed = await store.MutateAsync(state =>
        {
            var gone = new List<ImageRecord>();
            foreach (var ev in state.Events.Where(InScope))
                foreach (var image in Select(state, ev, now))
                {
                    state.Images.Remove(image.Id);
                    gone.Add(image);
                }
            return gone;
        }, ct);

        foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);

        var counts = removed.GroupBy(i => i.EventId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (eventId, count) in counts)
            logger.LogInformation("Retention removed {Count} images from event {EventId}.", count, eventId);
        return counts;
    }
}
