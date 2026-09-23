using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class EventScope
{
    public static IResult UnknownEvent() => Results.NotFound(new { error = "Ukjent arrangement." });
}
