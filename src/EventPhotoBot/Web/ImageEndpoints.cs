using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class ImageEndpoints
{
    public static void MapImages(this WebApplication app)
    {
        app.MapGet("/img/{id}/display", (string id, HttpContext http, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Display(id), http, objects, ct));

        app.MapGet("/img/{id}/thumb", (string id, HttpContext http, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Thumb(id), http, objects, ct));
    }

    /// <summary>
    /// Bytes are proxied rather than served from public bucket URLs so the password
    /// gate covers them. A given id's bytes never change, so they cache hard —
    /// which is what keeps this off the hot path despite the proxying.
    ///
    /// The cache header is set here, on the success path only, rather than in a
    /// blanket "/img" middleware: a middleware set ahead of the auth gate and the
    /// object lookup would stamp the same one-year immutable directive onto a 401
    /// (no session yet) or a 404 (no such id yet), and browsers do honour max-age
    /// on error responses — with `immutable` even suppressing revalidation, a
    /// viewer who hit the image before logging in could have that 401 cached for
    /// a year with no way to refresh it.
    /// </summary>
    private static async Task<IResult> Serve(
        string path, HttpContext http, IObjectStore objects, CancellationToken ct)
    {
        var stream = await objects.OpenReadAsync(path, ct);
        if (stream is null) return Results.NotFound();

        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Stream(stream, "image/jpeg", enableRangeProcessing: false);
    }
}
