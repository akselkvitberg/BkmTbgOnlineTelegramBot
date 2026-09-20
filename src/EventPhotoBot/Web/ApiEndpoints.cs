using EventPhotoBot.Imaging;
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record StatusRequest(string Status);
public sealed record PinRequest(string Pin);
public sealed record TakeoverRequest(string ImageId, int? Minutes);

public sealed record SettingsPatch(
    int? SlideSeconds,
    int? TransitionMs,
    string? Order,
    bool? NewestFirstBoost,
    int? RecurringEvery,
    bool? AutoApproveTrusted,
    bool? PairingMode,
    List<WhitelistEntry>? Whitelist,
    bool? ClearSeenSenders);

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

        app.MapPost("/api/images/{id}/status",
            async (string id, StatusRequest request, StateStore store) =>
            {
                if (!Enum.TryParse<ImageStatus>(request.Status, ignoreCase: true, out var status)
                    || status == ImageStatus.Pending)
                    return Results.BadRequest(
                        new { error = "status must be approved, hidden or rejected." });

                return await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var image)) return Results.NotFound();

                    image.Status = status;
                    image.DecidedAt = DateTimeOffset.UtcNow;

                    // An image that is no longer approved cannot be holding the screen.
                    if (status != ImageStatus.Approved) ClearTakeoverIfHeldBy(state, id);

                    return Results.Ok();
                });
            });

        app.MapPost("/api/images/{id}/pin",
            async (string id, PinRequest request, StateStore store) =>
            {
                // "takeover" is deliberately not a pin value — see PUT /api/takeover.
                if (!Enum.TryParse<PinKind>(request.Pin, ignoreCase: true, out var pin))
                    return Results.BadRequest(new { error = "pin must be none or recurring." });

                return await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var image)) return Results.NotFound();
                    image.Pin = pin;
                    return Results.Ok();
                });
            });

        app.MapPut("/api/takeover", async (TakeoverRequest request, StateStore store) =>
            await store.MutateAsync(state =>
            {
                if (!state.Images.TryGetValue(request.ImageId, out var image))
                    return Results.NotFound();

                // Takeover implies the image is on screen, so it is approved by definition.
                if (image.Status != ImageStatus.Approved)
                {
                    image.Status = ImageStatus.Approved;
                    image.DecidedAt = DateTimeOffset.UtcNow;
                }

                state.Settings.TakeoverImageId = request.ImageId;
                state.Settings.TakeoverUntil = request.Minutes is { } minutes
                    ? DateTimeOffset.UtcNow.AddMinutes(minutes)
                    : null;

                return Results.Ok();
            }));

        app.MapDelete("/api/takeover", async (StateStore store) =>
        {
            await store.MutateAsync(state =>
            {
                state.Settings.TakeoverImageId = null;
                state.Settings.TakeoverUntil = null;
            });
            return Results.Ok();
        });

        app.MapDelete("/api/images/{id}",
            async (string id, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                var removed = await store.MutateAsync(state =>
                {
                    if (!state.Images.Remove(id, out var image)) return (ImageRecord?)null;
                    ClearTakeoverIfHeldBy(state, id);
                    return image;
                });

                if (removed is null) return Results.NotFound();

                // State first here, unlike ingest: an entry pointing at deleted bytes
                // would put a broken image on the projector, while orphaned bytes are
                // invisible and the lifecycle rule sweeps them up.
                await objects.DeleteAsync(ObjectPaths.Display(id), ct);
                await objects.DeleteAsync(ObjectPaths.Thumb(id), ct);
                await objects.DeleteAsync(ObjectPaths.Original(id, removed.OriginalExtension), ct);

                return Results.Ok();
            });

        app.MapPost("/api/images",
            async (HttpRequest http, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected a file upload." });

                var form = await http.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "No file supplied." });

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var original = buffer.ToArray();

                ProcessedImage processed;
                try
                {
                    processed = ImagePipeline.Process(original);
                }
                catch
                {
                    return Results.BadRequest(new { error = "That file is not an image I can read." });
                }

                var id = Ulid.NewUlid().ToString();
                var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(extension)) extension = "jpg";
                var now = DateTimeOffset.UtcNow;

                await objects.WriteAsync(ObjectPaths.Original(id, extension),
                    original, "application/octet-stream", null, ct);
                await objects.WriteAsync(ObjectPaths.Display(id), processed.Display, "image/jpeg", null, ct);
                await objects.WriteAsync(ObjectPaths.Thumb(id), processed.Thumb, "image/jpeg", null, ct);

                await store.MutateAsync(state => state.Images[id] = new ImageRecord
                {
                    Id = id,
                    Source = ImageSource.Admin,
                    SenderId = null,
                    SenderName = null,
                    FileUniqueId = null,
                    Sha256 = processed.Sha256,
                    Caption = null,
                    Status = ImageStatus.Approved,
                    Pin = PinKind.None,
                    Width = processed.Width,
                    Height = processed.Height,
                    ReceivedAt = now,
                    DecidedAt = now,
                    SortKey = id,
                    OriginalExtension = extension,
                });

                return Results.Ok(new { id });
            }).DisableAntiforgery();

        app.MapPatch("/api/settings", async (SettingsPatch patch, StateStore store) =>
        {
            if (patch.Order is { } order
                && order is not ("shuffle" or "newest-first"))
                return Results.BadRequest(new { error = "order must be shuffle or newest-first." });

            await store.MutateAsync(state =>
            {
                var s = state.Settings;
                if (patch.SlideSeconds is { } slideSeconds) s.SlideSeconds = Math.Clamp(slideSeconds, 2, 120);
                if (patch.TransitionMs is { } transitionMs) s.TransitionMs = Math.Clamp(transitionMs, 0, 5000);
                if (patch.Order is { } o) s.Order = o == "newest-first" ? SlideOrder.NewestFirst : SlideOrder.Shuffle;
                if (patch.NewestFirstBoost is { } boost) s.NewestFirstBoost = boost;
                if (patch.RecurringEvery is { } every) s.RecurringEvery = Math.Clamp(every, 1, 100);
                if (patch.AutoApproveTrusted is { } auto) s.AutoApproveTrusted = auto;
                if (patch.PairingMode is { } pairing) s.PairingMode = pairing;
                if (patch.Whitelist is { } whitelist) s.Whitelist = whitelist;
                if (patch.ClearSeenSenders is true) s.SeenSenders.Clear();
            });

            return Results.Ok();
        });
    }

    private static void ClearTakeoverIfHeldBy(EventState state, string id)
    {
        if (state.Settings.TakeoverImageId != id) return;
        state.Settings.TakeoverImageId = null;
        state.Settings.TakeoverUntil = null;
    }
}
