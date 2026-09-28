using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public static class Routing
{
    public const string CallbackPrefix = "ev:";

    /// <summary>One button per other open event the sender belongs to; none when there is nothing to switch to.</summary>
    public static IReadOnlyList<InlineButton> SwitchButtons(EventState state, Sender sender, string? currentEventId, DateTimeOffset now) =>
        [.. sender.Memberships
            .Select(m => state.Find(m.EventId))
            .OfType<Event>()
            .Where(e => e.Id != currentEventId && e.IsOpen(now))
            .OrderByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .Select(e => new InlineButton(e.Name, CallbackPrefix + e.Id))];

    /// <summary>
    /// Where a private-chat photo goes: the sender's current event while it is open;
    /// otherwise the default event, but only for someone who joined it — a wedding
    /// guest who never scanned the church's code must not end up on the church screen;
    /// otherwise nowhere.
    /// </summary>
    public static Event? ResolvePrivateTarget(EventState state, Sender sender, DateTimeOffset now)
    {
        if (state.Find(sender.CurrentEventId) is { } current
            && sender.MembershipIn(current.Id) is not null
            && current.IsOpen(now))
            return current;

        var fallback = state.Default();
        return sender.MembershipIn(fallback.Id) is not null && fallback.IsOpen(now) ? fallback : null;
    }
}
