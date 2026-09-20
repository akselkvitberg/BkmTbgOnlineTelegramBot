using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class ImageEndpoints
{
    public static void MapImages(this WebApplication app)
    {
        app.MapGet("/img/{id}/display", (string id, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Display(id), objects, ct));

        app.MapGet("/img/{id}/thumb", (string id, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Thumb(id), objects, ct));
    }

    /// <summary>
    /// Bytes are proxied rather than served from public bucket URLs so the password
    /// gate covers them. A given id's bytes never change, so they cache hard —
    /// which is what keeps this off the hot path despite the proxying.
    /// </summary>
    private static async Task<IResult> Serve(string path, IObjectStore objects, CancellationToken ct)
    {
        var stream = await objects.OpenReadAsync(path, ct);
        if (stream is null) return Results.NotFound();

        return Results.Stream(stream, "image/jpeg", enableRangeProcessing: false);
    }
}
