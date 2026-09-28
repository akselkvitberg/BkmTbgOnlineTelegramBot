using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class EventScope
{
    public static IResult UnknownEvent() => Results.NotFound(new { error = "Ukjent arrangement." });

    /// <summary>
    /// The event a request names with ?event=, or the default event when it names none,
    /// so every page and screen written before events keeps meaning the church's own.
    /// Null for an id no event has.
    /// </summary>
    public static Event? Resolve(EventState state, string? eventId) =>
        string.IsNullOrEmpty(eventId) ? state.Default() : state.Find(eventId);
}
