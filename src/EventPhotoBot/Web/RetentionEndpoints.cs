using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record RetentionRequest(int? MaxAgeDays, int? KeepNewest);

public static class RetentionEndpoints
{
    public static void MapRetention(this WebApplication app)
    {
        // Called once a day by Cloud Scheduler. Outside the session gate, so it carries
        // its own secret; with none configured the route does not exist at all.
        app.MapPost("/internal/retention",
            async (HttpContext http, AppConfig config, StateStore store, IObjectStore objects,
                ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (config.RetentionSecret is not { } secret) return Results.NotFound();
                if (!SecretComparison.Matches(secret, http.Request.Headers["X-Retention-Secret"]))
                    return Results.Unauthorized();

                var counts = await RetentionSweep.RunAsync(store, objects,
                    loggers.CreateLogger("Retention"), null, DateTimeOffset.UtcNow, ct);
                return Results.Ok(new { deleted = counts });
            });

        app.MapPut("/api/events/{id}/retention", async (string id, RetentionRequest request, StateStore store) =>
        {
            if (request.MaxAgeDays is { } days && days is < 1 or > 3650)
                return EventEndpoints.BadRequest("maxAgeDays må være mellom 1 og 3650, eller tom.");
            var keep = Math.Clamp(request.KeepNewest ?? 0, 0, 10_000);
            if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();

            return await store.MutateAsync(state =>
            {
                if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
                ev.Retention = new Retention { MaxAgeDays = request.MaxAgeDays, KeepNewest = keep };
                return Results.Ok();
            });
        });

        app.MapPost("/api/events/{id}/retention/run",
            async (string id, StateStore store, IObjectStore objects, ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();
                var counts = await RetentionSweep.RunAsync(store, objects,
                    loggers.CreateLogger("Retention"), id, DateTimeOffset.UtcNow, ct);
                return Results.Ok(new { deleted = counts.GetValueOrDefault(id) });
            });
    }
}
