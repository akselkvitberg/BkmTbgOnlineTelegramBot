using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public static class Routing
{
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
