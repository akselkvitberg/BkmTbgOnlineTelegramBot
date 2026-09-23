using EventPhotoBot.Imaging;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace EventPhotoBot.Web;

public sealed record StatusRequest(string Status);
public sealed record PinRequest(string Pin);
public sealed record TakeoverRequest(string ImageId, int? Minutes);
public sealed record BanRequest(bool? Banned);
public sealed record MembershipRequest(bool? AutoApprove);

/// <summary>An event id routes the group, "" un-routes it; null (or missing) is a 400, so a malformed body cannot un-route a group.</summary>
public sealed record GroupEventRequest(string? EventId);

/// <summary>
/// The roster is deliberately absent here. An array replacement cannot carry the
/// ban cascade, so allowing it would be a second write path that silently skips
/// revoking a banned sender's photos — see POST /api/senders/{id}/ban.
/// </summary>
public sealed record SettingsPatch(
    string? EventName,
    int? SlideSeconds,
    int? TransitionMs,
    string? Order,
    bool? NewestFirstBoost,
    int? RecurringEvery,
    bool? KenBurns,
    bool? ShowJoinInvite,
    bool? ShowEventName,
    string? Layout);

public static class ApiEndpoints
{
    // A fat-fingered takeover duration (600 typed for 60) is exactly the failure the
    // takeover banner exists to catch after the fact; clamping up front means a typo
    // strands a photo for at most a day, not indefinitely.
    private const int MaxTakeoverMinutes = 24 * 60;

    private static readonly HashSet<string> AllowedUploadExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "webp" };

    /// <summary>
    /// Parses one of an enum's declared names, case-insensitively. The only way an
    /// enum should be read off the wire in this app.
    ///
    /// Enum.TryParse on its own is not that check, which is the trap: it also accepts
    /// a numeric string, so a body of {"status": "99"} comes back true with
    /// (ImageStatus)99 — a value no switch arm, no ToString() and no admin page has
    /// ever heard of. It would be written into state.json, survive a restart, and be
    /// served back to every client as the status of a real photo. Enum.IsDefined is
    /// what closes that, and every call site below goes through here so the rule is
    /// one thing rather than five that can drift.
    ///
    /// Enum.IsDefined alone would not finish the job, which is why the digit check
    /// comes first: "99" is caught by IsDefined, but "0" and "1" land on declared
    /// values and would quietly mean Pending, or None, or Known. No client sends a
    /// number and no admin page produces one, so a name that begins like a number is
    /// not a name at all. IsDefined still earns its place behind it — TryParse also
    /// accepts a comma-separated list and ORs the results together, even for an enum
    /// that is not [Flags], and "pending,approved" is not a declared name either.
    ///
    /// A null name is simply not a declared name: System.Text.Json deserializes a
    /// missing property into null regardless of the record's nullable annotation, so
    /// this is reached in practice and not only by a client sending literal null.
    /// </summary>
    private static bool TryParseName<T>(string? name, out T value) where T : struct, Enum
    {
        value = default;

        // Trimmed before the digit check, not after: Enum.TryParse trims for itself,
        // so " 0 " would otherwise slip past a check that only looked at the space.
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (char.IsAsciiDigit(trimmed[0]) || trimmed[0] is '-' or '+') return false;

        if (!Enum.TryParse<T>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
            return false;

        value = parsed;
        return true;
    }

    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/images",
            (string? status, [FromQuery(Name = "event")] string? eventId, StateStore store) =>
        {
            var images = store.Snapshot.Images.Values.AsEnumerable();

            if (status is not null)
            {
                if (!TryParseName<ImageStatus>(status, out var wanted))
                    return Results.BadRequest(new { error = "Ukjent status." });
                images = images.Where(i => i.Status == wanted);
            }

            if (!string.IsNullOrEmpty(eventId))
            {
                if (store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();
                images = images.Where(i => i.EventId == eventId);
            }

            return Results.Ok(images
                .OrderByDescending(i => i.SortKey, StringComparer.Ordinal)
                .Select(i => new
                {
                    i.Id, i.EventId, i.SenderId, i.SenderName, i.Caption, i.Width, i.Height,
                    Status = i.Status.ToString().ToLowerInvariant(),
                    Pin = i.Pin.ToString().ToLowerInvariant(),
                    i.ReceivedAt,
                }));
        });

        app.MapGet("/api/settings", ([FromQuery(Name = "event")] string? eventId, StateStore store) =>
        {
            var state = store.Snapshot;
            if (EventScope.Resolve(state, eventId) is not { } ev) return EventScope.UnknownEvent();
            var s = ev.Settings;
            return Results.Ok(new
            {
                EventId = ev.Id,
                EventName = ev.Name,
                s.SlideSeconds,
                s.TransitionMs,
                Order = s.Order == SlideOrder.NewestFirst ? "newest-first" : "shuffle",
                s.NewestFirstBoost,
                s.RecurringEvery,
                s.KenBurns,
                s.ShowJoinInvite,
                s.ShowEventName,
                Layout = s.Layout.ToString().ToLowerInvariant(),
                s.TakeoverImageId,
                s.TakeoverUntil,
                Senders = state.Senders.Select(sender => new
                {
                    sender.Id,
                    sender.Name,
                    sender.FirstSeen,
                    sender.Banned,
                    sender.CurrentEventId,
                    Memberships = sender.Memberships.Select(m => new { m.EventId, m.AutoApprove }),
                }),
                Groups = state.Groups.Select(group => new
                {
                    group.Id,
                    group.Title,
                    group.EventId,
                    group.FirstSeen,
                }),
            });
        });

        // Asked of Telegram on each call rather than cached from startup: the answer
        // changes when someone flips privacy mode in BotFather, and the admin page
        // that shows it is opened a handful of times, not polled.
        app.MapGet("/api/telegram/bot", async (ITelegramClient telegram, CancellationToken ct) =>
        {
            BotProfile? profile;
            try
            {
                profile = await telegram.GetMeAsync(ct);
            }
            catch (HttpRequestException)
            {
                profile = null;
            }

            return profile is null
                ? Results.Json(new { error = "Fikk ikke svar fra Telegram." }, statusCode: StatusCodes.Status502BadGateway)
                : Results.Ok(new { profile.Username, profile.CanReadAllGroupMessages });
        });

        app.MapPost("/api/groups/{id:long}/event",
            async (long id, GroupEventRequest request, StateStore store, ITelegramClient telegram, CancellationToken ct) =>
            {
                if (request.EventId is not { } eventId)
                    return Results.BadRequest(new { error = "eventId må være en arrangement-id, eller tom for å koble fra." });
                if (eventId != "" && store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();

                // Only a group the bot is actually in. Creating a row by id here would
                // let the list claim a group the bot cannot hear.
                var (result, notice) = await store.MutateAsync(state =>
                {
                    var group = state.Groups.FirstOrDefault(g => g.Id == id);
                    if (group is null) return (Results.NotFound(), (string?)null);
                    if (eventId == "")
                    {
                        group.EventId = null;
                        return (Results.Ok(), null);
                    }
                    if (state.Find(eventId) is not { } ev) return (EventScope.UnknownEvent(), null);
                    return Groups.Route(state, id, null, DateTimeOffset.UtcNow, ev.Id)
                        ? (Results.Ok(), Groups.NoticeFor(ev))
                        : (Results.Ok(), null);
                }, ct);

                // Told in the group itself, once per change: its members did not choose this.
                if (notice is not null) await telegram.SendMessageAsync(id, notice, ct);
                return result;
            });

        app.MapPost("/api/groups/{id:long}/leave",
            async (long id, StateStore store, ITelegramClient telegram, CancellationToken ct) =>
            {
                if (store.Snapshot.Groups.All(g => g.Id != id)) return Results.NotFound();

                // Telegram first: if it refuses, the bot is still in the group and the
                // row has to stay so the page keeps saying so.
                if (!await telegram.LeaveChatAsync(id, ct))
                    return Results.Json(new { error = "Telegram lot ikke boten forlate gruppen." },
                        statusCode: StatusCodes.Status502BadGateway);

                // The my_chat_member update that follows would remove it too; doing it
                // here as well means the page is right the moment this returns.
                await store.MutateAsync(state => state.Groups.RemoveAll(g => g.Id == id), ct);
                return Results.Ok();
            });

        app.MapGet("/api/manifest",
            (HttpContext http, [FromQuery(Name = "event")] string? eventId, StateStore store, BotIdentity identity) =>
        {
            // Served entirely from memory. No object-store I/O on this path, ever:
            // it runs every two seconds per open page for the length of the event.
            var state = store.Snapshot;
            if (EventScope.Resolve(state, eventId) is not { } ev) return EventScope.UnknownEvent();
            var now = DateTimeOffset.UtcNow;

            // The generation alone is not enough: which event this is, and whether it is
            // open, change what the screen gets — and an event opens and closes on its
            // own clock, with no write to move the generation.
            var etag = $"\"{store.Generation}-{ev.Id}-{(ev.IsOpen(now) ? "open" : "closed")}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(ManifestBuilder.Build(state, ev, store.Generation, now, identity.JoinUrlFor(ev.JoinCode)));
        });

        app.MapPost("/api/images/{id}/status",
            async (string id, StatusRequest request, StateStore store,
                ITelegramClient telegram, ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (!TryParseName<ImageStatus>(request.Status, out var status)
                    || status == ImageStatus.Pending)
                    return Results.BadRequest(
                        new { error = "status må være approved, hidden eller rejected." });

                var (result, image) = await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var img)) return (Results.NotFound(), (ImageRecord?)null);

                    img.Status = status;
                    img.DecidedAt = DateTimeOffset.UtcNow;

                    // An image that is no longer approved cannot be holding the screen.
                    if (status != ImageStatus.Approved) ClearTakeoverIfHeldBy(state, id);

                    return (Results.Ok(), img);
                });

                if (image is not null)
                    await Reactions.SyncAsync(telegram, image, loggers.CreateLogger("Reactions"), ct);

                return result;
            });

        app.MapPost("/api/images/{id}/pin",
            async (string id, PinRequest request, StateStore store) =>
            {
                // "takeover" is deliberately not a pin value — see PUT /api/takeover.
                if (!TryParseName<PinKind>(request.Pin, out var pin))
                    return Results.BadRequest(new { error = "pin må være none eller recurring." });

                return await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var image)) return Results.NotFound();
                    image.Pin = pin;
                    return Results.Ok();
                });
            });

        app.MapPut("/api/takeover", async (TakeoverRequest request, StateStore store,
            ITelegramClient telegram, ILoggerFactory loggers, CancellationToken ct) =>
        {
            // Guard before the dictionary lookup: System.Text.Json happily deserializes
            // a missing or explicitly null "imageId" into ImageId = null (the record's
            // non-nullable annotation isn't enforced at runtime), and
            // Dictionary<string,T>.TryGetValue(null, ...) throws ArgumentNullException
            // rather than returning false. Without this check that becomes an unhandled
            // 500 — there is no exception-handler middleware in this app.
            if (string.IsNullOrEmpty(request.ImageId))
                return Results.BadRequest(new { error = "imageId er påkrevd." });

            var (result, image) = await store.MutateAsync(state =>
            {
                if (!state.Images.TryGetValue(request.ImageId, out var img))
                    return (Results.NotFound(), (ImageRecord?)null);

                // Takeover implies the image is on screen, so it is approved by definition.
                if (img.Status != ImageStatus.Approved)
                {
                    img.Status = ImageStatus.Approved;
                    img.DecidedAt = DateTimeOffset.UtcNow;
                }

                var settings = state.EventOf(img).Settings;
                settings.TakeoverImageId = request.ImageId;
                settings.TakeoverUntil = request.Minutes is { } minutes
                    ? DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(minutes, 1, MaxTakeoverMinutes))
                    : null;

                return (Results.Ok(), img);
            });

            if (image is not null)
                await Reactions.SyncAsync(telegram, image, loggers.CreateLogger("Reactions"), ct);

            return result;
        });

        app.MapDelete("/api/takeover",
            async ([FromQuery(Name = "event")] string? eventId, StateStore store) =>
        {
            if (EventScope.Resolve(store.Snapshot, eventId) is not { } target) return EventScope.UnknownEvent();

            // StateStore.MutateAsync always writes, even when the mutation callback
            // changes nothing - so guarding inside the callback would not have
            // avoided the write. Skipping the call outright when there is plainly
            // nothing to clear is what actually avoids bumping the generation and
            // forcing every connected client to refetch for a no-op clear (the admin
            // UI can call this more than once - two admins, or a stale page). Reading
            // Snapshot outside the lock is the same fast-path pattern used elsewhere
            // in this codebase: not authoritative by itself, but MutateAsync's own
            // clone-and-check inside the lock would just no-op harmlessly on the rare
            // race where a takeover appears between this check and the call.
            if (target.Settings.TakeoverImageId is null) return Results.Ok();

            await store.MutateAsync(state =>
            {
                if (state.Find(target.Id) is not { } ev) return;
                ev.Settings.TakeoverImageId = null;
                ev.Settings.TakeoverUntil = null;
            });
            return Results.Ok();
        });

        app.MapDelete("/api/images/{id}",
            async (string id, StateStore store, IObjectStore objects,
                ITelegramClient telegram, ILoggerFactory loggers, CancellationToken ct) =>
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
                await ImageObjects.DeleteAsync(objects, removed, ct);

                await Reactions.ClearAsync(telegram, removed, loggers.CreateLogger("Reactions"), ct);

                return Results.Ok();
            });

        // Clearing the slate between events. One state write for the lot, so the
        // screen goes from every photo to none in a single generation rather than
        // thinning out one delete at a time.
        // Reactions are left alone on bulk deletes: hundreds of calls inside one
        // request would meet Telegram's rate limit and the request timeout.
        app.MapDelete("/api/images",
            async ([FromQuery(Name = "event")] string? eventId, StateStore store, IObjectStore objects,
                CancellationToken ct) =>
            {
                if (EventScope.Resolve(store.Snapshot, eventId) is not { } ev) return EventScope.UnknownEvent();
                var targetId = ev.Id;
                if (!store.Snapshot.Images.Values.Any(i => i.EventId == targetId)) return Results.Ok(new { deleted = 0 });

                var removed = await store.MutateAsync(state =>
                {
                    var images = state.Images.Values.Where(i => i.EventId == targetId).ToList();
                    foreach (var image in images) state.Images.Remove(image.Id);
                    var settings = state.Find(targetId)!.Settings;
                    settings.TakeoverImageId = null;
                    settings.TakeoverUntil = null;
                    return images;
                });

                // State first, for the same reason as the single delete above.
                foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);

                return Results.Ok(new { deleted = removed.Count });
            });

        app.MapPost("/api/images",
            async (HttpRequest http, [FromQuery(Name = "event")] string? eventId, StateStore store,
                IObjectStore objects, CancellationToken ct) =>
            {
                if (EventScope.Resolve(store.Snapshot, eventId) is not { } target) return EventScope.UnknownEvent();
                if (!http.HasFormContentType) return Results.BadRequest(new { error = "Forventet en filopplasting." });

                var form = await http.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "Ingen fil ble sendt med." });

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var original = buffer.ToArray();

                ProcessedImage processed;
                try
                {
                    processed = ImagePipeline.Process(original);
                }
                catch (ImageTooLargeException e)
                {
                    // The pipeline writes this message for the sender's eyes; folding it
                    // into the generic reply below would tell someone their perfectly
                    // good photo is unreadable.
                    return Results.BadRequest(new { error = e.Message });
                }
                catch
                {
                    // The upload page is used from a phone, where this is the one
                    // rejection that happens for a reason the uploader can act on.
                    return Results.BadRequest(new
                    {
                        error = ImagePipeline.LooksLikeHeif(original)
                            ? "HEIC-bilder kan jeg ikke lese. Velg bildet fra Fotobibliotek "
                              + "i stedet for Filer, eller sett Innstillinger → Kamera → Formater "
                              + "til «Mest kompatibelt»."
                            : "Den filen er ikke et bilde jeg kan lese.",
                    });
                }

                var id = Ulid.NewUlid().ToString();
                // The object name is built from this, so it must come from a fixed
                // list, not verbatim from the client-supplied IFormFile.FileName —
                // an attacker-controlled string with no validation otherwise ends up
                // as part of a GCS object path.
                var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
                if (!AllowedUploadExtensions.Contains(extension)) extension = "jpg";
                var now = DateTimeOffset.UtcNow;
                var targetId = target.Id;

                await objects.WriteAsync(ObjectPaths.Original(id, extension),
                    original, "application/octet-stream", null, ct);
                await objects.WriteAsync(ObjectPaths.Display(id), processed.Display, "image/jpeg", null, ct);
                await objects.WriteAsync(ObjectPaths.Thumb(id), processed.Thumb, "image/jpeg", null, ct);

                await store.MutateAsync(state => state.Images[id] = new ImageRecord
                {
                    Id = id,
                    EventId = targetId,
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

        app.MapPatch("/api/settings",
            async (SettingsPatch patch, [FromQuery(Name = "event")] string? eventId, StateStore store) =>
        {
            if (EventScope.Resolve(store.Snapshot, eventId) is not { } target) return EventScope.UnknownEvent();

            if (patch.Order is { } order
                && order is not ("shuffle" or "newest-first"))
                return Results.BadRequest(new { error = "order må være shuffle eller newest-first." });

            // Parsed up front rather than inside the mutation: an unknown name must be
            // a 400 the admin page can show, not a silently ignored field that leaves
            // the picker claiming a layout the screen is not running.
            SlideLayout? layout = null;
            if (patch.Layout is { } layoutName)
            {
                if (!TryParseName<SlideLayout>(layoutName, out var parsed))
                    return Results.BadRequest(new
                    {
                        error = "layout må være single, mosaic, polaroid, filmstrip, "
                                + "collage eller split.",
                    });
                layout = parsed;
            }

            // Validated before the mutation, like the name check above: an empty name
            // must be a 400 that leaves the existing name untouched, not a value the
            // mutation callback has to reject after already committing other fields.
            string? name = null;
            if (patch.EventName is { } eventName && (name = EventEndpoints.CleanName(eventName)) is null)
                return Results.BadRequest(new { error = "Navnet kan ikke være tomt." });
            var targetId = target.Id;

            await store.MutateAsync(state =>
            {
                if (state.Find(targetId) is not { } ev) return;
                var s = ev.Settings;
                if (name is not null) ev.Name = name;
                if (patch.SlideSeconds is { } slideSeconds) s.SlideSeconds = Math.Clamp(slideSeconds, 2, 120);
                if (patch.TransitionMs is { } transitionMs) s.TransitionMs = Math.Clamp(transitionMs, 0, 5000);
                if (patch.Order is { } o) s.Order = o == "newest-first" ? SlideOrder.NewestFirst : SlideOrder.Shuffle;
                if (patch.NewestFirstBoost is { } boost) s.NewestFirstBoost = boost;
                if (patch.RecurringEvery is { } every) s.RecurringEvery = Math.Clamp(every, 1, 100);
                if (patch.KenBurns is { } kenBurns) s.KenBurns = kenBurns;
                if (patch.ShowJoinInvite is { } showJoinInvite) s.ShowJoinInvite = showJoinInvite;
                if (patch.ShowEventName is { } showEventName) s.ShowEventName = showEventName;
                if (layout is { } chosen) s.Layout = chosen;
            });

            return Results.Ok();
        });

        app.MapPost("/api/senders/{id:long}/ban",
            async (long id, BanRequest request, StateStore store, ITelegramClient telegram,
                ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (request.Banned is not { } banned)
                    return Results.BadRequest(new { error = "banned må være true eller false." });

                var rejected = await store.MutateAsync(state =>
                {
                    var sender = state.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // Creating on write is how an organiser pre-bans a nuisance
                        // before that person has ever messaged the bot.
                        sender = new Sender { Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow };
                        state.Senders.Add(sender);
                    }

                    // Memberships are left as they are, so an unban restores them.
                    sender.Banned = banned;
                    if (!banned) return [];

                    // A ban revokes what they already sent, in every event and in this
                    // same write, so no screen can be showing a banned sender's photo
                    // between two state generations.
                    var now = DateTimeOffset.UtcNow;
                    var images = state.Images.Values.Where(i => i.SenderId == id).ToList();
                    foreach (var image in images)
                    {
                        image.Status = ImageStatus.Rejected;
                        image.DecidedAt = now;
                        ClearTakeoverIfHeldBy(state, image.Id);
                    }
                    return images;
                }, ct);

                var log = loggers.CreateLogger("Reactions");
                foreach (var image in rejected) await Reactions.SyncAsync(telegram, image, log, ct);
                return Results.Ok();
            });

        app.MapPost("/api/senders/{id:long}/memberships/{eventId}",
            async (long id, string eventId, MembershipRequest request, StateStore store) =>
            {
                if (request.AutoApprove is not { } autoApprove)
                    return Results.BadRequest(new { error = "autoApprove må være true eller false." });
                if (store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();

                return await store.MutateAsync(state =>
                {
                    if (state.Find(eventId) is null) return EventScope.UnknownEvent();
                    var sender = state.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // How an organiser pre-approves a photographer for one event.
                        sender = new Sender { Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow };
                        state.Senders.Add(sender);
                    }
                    var membership = sender.MembershipIn(eventId);
                    if (membership is null)
                    {
                        membership = new Membership { EventId = eventId };
                        sender.Memberships.Add(membership);
                    }
                    membership.AutoApprove = autoApprove;
                    return Results.Ok();
                });
            });
    }

    private static void ClearTakeoverIfHeldBy(EventState state, string id)
    {
        foreach (var ev in state.Events)
        {
            if (ev.Settings.TakeoverImageId != id) continue;
            ev.Settings.TakeoverImageId = null;
            ev.Settings.TakeoverUntil = null;
        }
    }
}
