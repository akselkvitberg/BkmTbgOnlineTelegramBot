using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/manifest", (HttpContext http, StateStore store) =>
        {
            // Served entirely from memory. No object-store I/O on this path, ever:
            // it runs every two seconds per open page for the length of the event.
            var etag = $"\"{store.Generation}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";

            return Results.Ok(ManifestBuilder.Build(
                store.Snapshot, store.Generation, DateTimeOffset.UtcNow));
        });
    }
}
