using System.IO.Compression;
using System.Text.RegularExpressions;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using Microsoft.AspNetCore.Http.Features;

namespace EventPhotoBot.Web;

public sealed record CreateEventRequest(string? Id, string? Name, DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);
public sealed record EventPatch(string? Name);

/// <summary>Replaces both times: a null clears one. The default event has no schedule.</summary>
public sealed record ScheduleRequest(DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);

public static class EventEndpoints
{
    public const int MaxNameLength = 100;
    private static readonly Regex SlugPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    /// <summary>
    /// Trimmed, and truncated rather than rejected past the limit, as the event name
    /// always was. Null when nothing is left: the bot names the event in every
    /// acknowledgement, so an event without a name would answer "Mottatt til ".
    /// </summary>
    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    internal static IResult BadRequest(string error) => Results.BadRequest(new { error });

    private static string? ScheduleError(DateTimeOffset? opensAt, DateTimeOffset? closesAt) =>
        opensAt is { } o && closesAt is { } c && o >= c
            ? "Starttidspunktet må være før sluttidspunktet."
            : null;

    public static void MapEvents(this WebApplication app)
    {
        app.MapGet("/api/events", (StateStore store, BotIdentity identity) =>
        {
            var state = store.Snapshot;
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(state.Events
                .OrderByDescending(e => e.IsDefault)
                .ThenByDescending(e => e.CreatedAt)
                .Select(e => View(state, e, now, identity)));
        });

        app.MapPost("/api/events", async (CreateEventRequest request, StateStore store) =>
        {
            var id = request.Id?.Trim() ?? "";
            if (!SlugPattern.IsMatch(id))
                return BadRequest("id må være 1–32 tegn: små bokstaver a–z, sifre og bindestrek.");
            if (CleanName(request.Name) is not { } name) return BadRequest("Navnet kan ikke være tomt.");
            if (ScheduleError(request.OpensAt, request.ClosesAt) is { } error) return BadRequest(error);

            var conflict = Results.Conflict(new { error = "Det finnes allerede et arrangement med den id-en." });
            if (store.Snapshot.Find(id) is not null) return conflict;

            return await store.MutateAsync(state =>
            {
                if (state.Find(id) is not null) return conflict;
                state.Events.Add(new Event
                {
                    Id = id,
                    Name = name,
                    JoinCode = EventRules.UniqueJoinCode(state),
                    OpensAt = request.OpensAt,
                    ClosesAt = request.ClosesAt,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                return Results.Created($"/api/events/{id}", new { id });
            });
        });

        app.MapPatch("/api/events/{id}", async (string id, EventPatch patch, StateStore store) =>
        {
            if (CleanName(patch.Name) is not { } name) return BadRequest("Navnet kan ikke være tomt.");
            return await Change(store, id, ev => { ev.Name = name; return null; });
        });

        app.MapPut("/api/events/{id}/schedule", async (string id, ScheduleRequest request, StateStore store) =>
        {
            if (ScheduleError(request.OpensAt, request.ClosesAt) is { } error) return BadRequest(error);
            return await Change(store, id, ev =>
            {
                if (ev.IsDefault) return BadRequest("Standardarrangementet har ingen tidsplan.");
                ev.OpensAt = request.OpensAt;
                ev.ClosesAt = request.ClosesAt;
                return null;
            });
        });

        app.MapPost("/api/events/{id}/close", async (string id, StateStore store) =>
            await Change(store, id, ev =>
            {
                if (ev.IsDefault) return BadRequest("Standardarrangementet kan ikke stenges.");
                ev.ClosedAt ??= DateTimeOffset.UtcNow;
                return null;
            }));

        app.MapPost("/api/events/{id}/reopen", async (string id, StateStore store) =>
            await Change(store, id, ev =>
            {
                var now = DateTimeOffset.UtcNow;
                ev.ClosedAt = null;
                // An end time already behind us would keep the event closed by its own
                // clock, and "Åpne igjen" would look like it did nothing.
                if (ev.ClosesAt <= now) ev.ClosesAt = null;
                return null;
            }));

        app.MapPost("/api/events/{id}/rotate-code", async (string id, StateStore store) =>
            await Change(store, id, ev => null, (state, ev) => ev.JoinCode = EventRules.UniqueJoinCode(state)));

        app.MapDelete("/api/events/{id}",
            async (string id, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                if (store.Snapshot.Find(id) is not { } target) return EventScope.UnknownEvent();
                if (target.IsDefault) return BadRequest("Standardarrangementet kan ikke slettes.");

                var removed = await store.MutateAsync(state =>
                {
                    if (state.Find(id) is not { IsDefault: false } ev) return null;
                    state.Events.Remove(ev);

                    var images = state.Images.Values.Where(i => i.EventId == id).ToList();
                    foreach (var image in images) state.Images.Remove(image.Id);
                    foreach (var sender in state.Senders)
                    {
                        sender.Memberships.RemoveAll(m => m.EventId == id);
                        if (sender.CurrentEventId == id) sender.CurrentEventId = null;
                    }
                    // Un-routed without a notice: the group is told when it is routed
                    // somewhere, not when it stops being collected from.
                    foreach (var group in state.Groups.Where(g => g.EventId == id)) group.EventId = null;
                    return images;
                }, ct);

                if (removed is null) return EventScope.UnknownEvent();
                foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);
                return Results.Ok(new { deleted = removed.Count });
            });

        // Streamed straight into the response, entry by entry: an event's originals
        // can run to gigabytes, and the instance has 1 GiB of memory, /tmp included.
        app.MapGet("/api/events/{id}/export.zip",
            async (string id, HttpContext http, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                var state = store.Snapshot;
                if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
                var images = state.Images.Values
                    .Where(i => i.EventId == ev.Id && i.Status == ImageStatus.Approved)
                    .OrderBy(i => i.ReceivedAt)
                    .ToList();

                http.Response.ContentType = "application/zip";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{ev.Id}.zip\"";

                // ZipArchiveEntry still closes each entry with a synchronous Write of its
                // data descriptor when the target stream can't seek (the response body
                // can't) — true even through the *Async API. Without this the write throws
                // "Synchronous operations are disallowed" on Kestrel and on the test host.
                http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

                await using (var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create,
                                 leaveOpen: true, entryNameEncoding: null, ct))
                {
                    foreach (var image in images)
                    {
                        await using var source = await objects.OpenReadAsync(
                            ObjectPaths.Original(image.Id, image.OriginalExtension), ct);
                        if (source is null) continue;   // bytes already gone; the rest are still worth having

                        // UTC, marked as such: the container may carry no time-zone data.
                        var name = $"{image.ReceivedAt.UtcDateTime:yyyyMMdd-HHmmss}Z-{image.Id}.{image.OriginalExtension}";
                        // Photos are already compressed; deflating them again costs CPU for nothing.
                        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        await using var target = await entry.OpenAsync(ct);
                        await source.CopyToAsync(target, ct);
                    }
                }

                return Results.Empty;
            });
    }

    /// <summary>
    /// One event changed in one write. <paramref name="check"/> may refuse with a
    /// result; <paramref name="apply"/> runs when the whole state is needed as well.
    /// </summary>
    private static async Task<IResult> Change(StateStore store, string id,
        Func<Event, IResult?> check, Action<EventState, Event>? apply = null)
    {
        if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();
        return await store.MutateAsync(state =>
        {
            if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
            if (check(ev) is { } refused) return refused;
            apply?.Invoke(state, ev);
            return Results.Ok();
        });
    }

    private static object View(EventState state, Event e, DateTimeOffset now, BotIdentity identity)
    {
        var images = state.Images.Values.Where(i => i.EventId == e.Id).ToList();
        return new
        {
            e.Id,
            e.Name,
            e.IsDefault,
            Phase = e.PhaseAt(now).ToString().ToLowerInvariant(),
            e.OpensAt,
            e.ClosesAt,
            e.ClosedAt,
            e.CreatedAt,
            e.JoinCode,
            JoinUrl = identity.JoinUrlFor(e.JoinCode),
            Retention = new { e.Retention.MaxAgeDays, e.Retention.KeepNewest },
            Counts = new
            {
                Total = images.Count,
                Approved = images.Count(i => i.Status == ImageStatus.Approved),
                Pending = images.Count(i => i.Status == ImageStatus.Pending),
            },
        };
    }
}
