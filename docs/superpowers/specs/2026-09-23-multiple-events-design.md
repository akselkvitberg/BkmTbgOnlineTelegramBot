# Multiple events — design

Turns the event from the deployment into data: a standing default event for the
church's daily activities, plus special events (a wedding, a concert) that run
alongside it, each with its own join code, screen, settings and photos.

## The problem

Today one deployment is one event. There is no event entity: the name is
`Settings.EventName`, the join code is the `JOIN_CODE` secret, and every image
sits in the one `EventState.Images` dictionary
([`Models.cs`](../../../src/EventPhotoBot/State/Models.cs)). The screen shows
all approved images and nothing else.

The app is used by a local church. Daily activities are posted and shown on a
screen in the church, continuously. When there is a wedding or another special
event, its photos belong to that event only: they must not appear on the daily
screen, and daily photos must not appear on the wedding's. Both run at the same
time, and the same person may be sending to either.

## Decisions taken

| Question | Decision |
|---|---|
| Can an image belong to more than one event? | No. Every image has exactly one `EventId`. |
| How is the daily screen modelled? | As the default event: exactly one, never closes, never deleted. |
| How is a group routed? | By an admin, in `/admin/telegram`. Posting a join code in a group does nothing. |
| How does a private sender pick an event? | `/start <code>` joins that event and makes it current; inline buttons switch between open memberships. |
| What happens when a sender's current event closes? | Silent fallback to the default event, if they are a member of it. Otherwise their photos are declined. |
| Does the acknowledgement name the event? | Yes: "Mottatt til {navn} — …". |
| How does a special event end? | An optional end time, and a manual close. Both. |
| What happens to a closed event's photos? | Kept, read-only, with a ZIP export, until an admin deletes the event. |
| What happens to old daily photos? | Deleted after N days, except the newest K approved, so the screen always has a pool. |
| Is auto-approve global? | No, per event membership. Ban stays global. |
| Storage | One `state.json`, as today. Split later if it grows past a few MB. |
| Group photo reactions | 👀 while pending, 🔥 once approved (by an admin or automatically), removed if rejected or hidden. |

## Data model

```csharp
public sealed class EventState
{
    public Dictionary<string, ImageRecord> Images { get; set; } = [];
    public List<Event> Events { get; set; } = [];
    public List<Sender> Senders { get; set; } = [];   // moved out of Settings
    public List<BotGroup> Groups { get; set; } = [];  // moved out of Settings

    /// Read only by the migration below; null on every write.
    [JsonPropertyName("settings")] public LegacySettings? Legacy { get; set; }
}

public sealed class Event
{
    public required string Id { get; set; }        // slug, [a-z0-9-]{1,32}, immutable
    public string Name { get; set; } = "";         // replaces Settings.EventName
    public bool IsDefault { get; set; }
    public required string JoinCode { get; set; }
    public DateTimeOffset? OpensAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }  // set by a manual close
    public EventSettings Settings { get; set; } = new();
    public Retention Retention { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Retention
{
    public int? MaxAgeDays { get; set; }  // null = off
    public int KeepNewest { get; set; }   // approved images kept regardless of age
}

public sealed class Sender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public bool Banned { get; set; }
    public string? CurrentEventId { get; set; }
    public List<Membership> Memberships { get; set; } = [];
}

public sealed class Membership
{
    public required string EventId { get; set; }
    public bool AutoApprove { get; set; }
}

public sealed class BotGroup
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? EventId { get; set; }          // replaces Listening; null = not collected
    public DateTimeOffset FirstSeen { get; set; }
}
```

`ImageRecord` gains:

- `EventId` (required after migration).
- `TelegramChatId` and `TelegramMessageId`, set for photos from groups only, so
  the bot can change its reaction when the photo is decided.

`EventSettings` is today's `Settings` without `EventName`, `Senders` and
`Groups`: layout, timing, order, Ken Burns, invite, event-name corner, takeover.
Each event's screen has its own.

`SenderStatus` is removed. A membership is what `Known` was, for one event;
`Membership.AutoApprove` is what `AutoApprove` was; `Sender.Banned` is what
`Banned` was.

### Open, closed, scheduled

Computed on every read, never stored as a status and never changed by a job:

```text
IsOpen(now) = ClosedAt is null
           && (OpensAt is null || OpensAt <= now)
           && (ClosesAt is null || ClosesAt > now)
```

- The default event has `OpensAt`, `ClosesAt` and `ClosedAt` null, and the API
  refuses to set them.
- Close now sets `ClosedAt`. Reopen clears `ClosedAt`, and clears `ClosesAt` if
  it is in the past.
- Times are stored in UTC and entered and shown in Europe/Oslo time.

### Join codes

- Generated with at least 128 bits of randomness, encoded in `[A-Za-z0-9_-]`,
  within Telegram's 64-character deep-link payload limit.
- Unique across all events. Matching checks every event's code in constant time,
  as `JoinCodeMatches` does today.
- Rotating an event's code replaces it; the old code stops working immediately.
  Existing members are unaffected.

### Migration

Runs on load, when `Events` is empty. Idempotent: a migrated file has events
and loads unchanged.

1. Create the default event from the legacy `Settings`: `Name` from
   `EventName`, display settings copied, `JoinCode` from `JOIN_CODE` if set,
   otherwise generated. Retention off, so no photo is deleted by surprise; an
   admin turns it on.
2. Set `EventId = default` on every image.
3. For every legacy sender: a membership in the default event,
   `AutoApprove = (Status == AutoApprove)`, `Banned = (Status == Banned)`,
   `CurrentEventId = default`.
4. Move groups across; `EventId = default` where `Listening` was true, null
   otherwise.
5. Null out `Legacy`, so the next write drops the old `settings` object.

`JOIN_CODE` becomes optional and is read only by step 1.

## Telegram

### Groups

- The bot being added to a group records it with `EventId = null`, as
  `Groups.Remember` does today. Nothing is collected from it.
- The join-code path in groups (`TryReadGroupJoinCode`, `StartListeningAsync`) is
  removed. Posting a code in a group does nothing.
- An admin sets the group's event. Every change to a different, non-null event
  posts the listening notice in the group, naming the event. Setting it to null
  posts nothing.
- A photo is collected only if the group has an event and that event is open.
  Otherwise it is ignored with no reaction and no reply. There is no fallback to
  the default event: a wedding group did not agree to the church screen.
- A member's first photo adds them to the roster with a membership in the
  group's event, in the same write as the image, as today. A known sender
  posting in a group routed to an event they are not a member of gets the
  membership added the same way.
- Supergroup migration carries `EventId` across. If both rows exist, the old
  row's non-null `EventId` wins over a null one.
- Reactions:

  | Image status | Reaction |
  |---|---|
  | Pending | 👀 |
  | Approved (manual or auto) | 🔥 |
  | Rejected, hidden, deleted | none (empty reaction list) |

  The admin status and delete endpoints set the reaction after the state write.
  Best effort: a failure (message deleted, bot removed, the emoji not allowed in
  that group) is logged and the decision stands.

### Private chat

- `/start <code>` matching an open event:
  - Adds the sender to the roster if absent, adds the membership if absent,
    sets `CurrentEventId`.
  - Replies "Du sender nå bilder til {navn}." with an inline keyboard of the
    sender's other open memberships, one button each. No keyboard if there are
    none.
- `/start <code>` matching a closed event: "{navn} er avsluttet." No write.
- `/start` with no code, from a known sender: the current event's name and the
  same keyboard.
- A button carries `callback_data = "ev:{eventId}"` (within Telegram's 64-byte
  limit, since slugs are at most 32 characters). On tap:
  - If the sender is a member and the event is open: set `CurrentEventId`, edit
    the message to "Du sender nå bilder til {navn}." with a fresh keyboard,
    answer the callback.
  - Otherwise: answer the callback with "{navn} er avsluttet." and change
    nothing.
- A photo resolves its event in this order:
  1. `CurrentEventId`, if the sender is a member and it is open.
  2. The default event, if the sender is a member and it is open. Silent: no
     message, and `CurrentEventId` is left as it is.
  3. Otherwise decline with "{navn} er avsluttet.", throttled like the join
     prompt.
- The event is resolved before the download and again inside `MutateAsync`,
  alongside the ban and duplicate checks. The acknowledgement names the event the
  image was actually stored under.
- Acknowledgement: "Mottatt til {navn} — det er på skjermen nå." or
  "Mottatt til {navn} — en arrangør godkjenner det snart." One per album, as
  today.

### Dedup

Both the fast-path checks and the authoritative check in `MutateAsync` compare
`FileUniqueId` and `Sha256` only among images with the target `EventId`. The same
photo can be sent to Daglig and to a wedding.

### Bans

A ban is global and rejects every image from that sender in every event, in the
same write, as today. Unbanning restores the sender's memberships as they were.

### Texts

Several texts say "Alt slettes etter arrangementet", which is not true for the
default event and not true in the same way for a special event.

- Private-chat replies drop the sentence.
- The group listening notice names the event and states its deletion rule:
  - Retention on: "Bildene slettes etter {N} dager."
  - Retention off: "Bildene slettes når arrangøren sletter arrangementet."

The church's privacy contact should review the final wording before release.

### Plumbing

- `TgUpdate.CallbackQuery` and the models it needs.
- `reply_markup` (inline keyboard) on `SendMessageAsync`.
- `AnswerCallbackQueryAsync` and `EditMessageTextAsync` on `ITelegramClient`,
  `TelegramClient` and `OfflineTelegramClient`.
- `setWebhook` in `infra/deploy.ps1` sets no `allowed_updates`, and Telegram's
  default includes `callback_query`, so no change there. Confirm this when
  implementing.
- The `/dev` simulator: choose which event's code to join with, and tap a button
  from a recorded reply.

## Admin

### Event context

- An event picker in the admin header, carried in `?event=`, defaulting to the
  default event.
- **Queue** shows pending images from all open events, each labelled with its
  event, with a filter.
- **Images**, **Settings** (display and retention), **takeover**, **delete all**
  and **upload** act on the selected event.

### `/admin/events` (new)

- Lists events grouped as open, scheduled and closed.
- Create: name, slug, optional opens and closes times. The code is generated.
- Close now, reopen. Neither for the default event.
- Join code: deep link, QR, rotate.
- Export ZIP: approved originals of the event, named
  `{ReceivedAt:yyyyMMdd-HHmmss}Z-{id}.{ext}`, streamed. No sender names in file
  names.
- Delete: requires typing the event's name. Not for the default event. In one
  state write: removes the event, its images, memberships in it, and clears
  `CurrentEventId` where it points to it; groups routed to it get
  `EventId = null` with no notice. Then deletes the image objects.

### `/admin/telegram`

- Groups: an event dropdown ("Ikke koblet" plus every event) replaces the
  listening toggle. Leave works as today.
- People: ban is global; memberships are listed per person with an auto-approve
  toggle each.

### API

| Endpoint | Change |
|---|---|
| `GET/POST /api/events`, `PATCH/DELETE /api/events/{id}` | New. `PATCH /api/events/{id}` sets the name only |
| `POST /api/events/{id}/close`, `/reopen`, `/rotate-code` | New |
| `PUT /api/events/{id}/schedule` | New: replaces `OpensAt`/`ClosesAt` |
| `PUT /api/events/{id}/retention` | New: replaces `MaxAgeDays`/`KeepNewest` |
| `POST /api/events/{id}/retention/run` | New: runs the retention sweep for one event now |
| `GET /api/events/{id}/export.zip` | New |
| `POST /api/groups/{id}/event` | Replaces `/api/groups/{id}/listening` |
| `POST /api/senders/{id}/ban` (`{ banned: bool }`) | Replaces `/api/senders/{id}/status` |
| `POST /api/senders/{id}/memberships/{eventId}` | New: auto-approve on or off |
| `GET /api/images` | `?event=` optional; absent means every event |
| `GET/PATCH /api/settings`, `PUT/DELETE /api/takeover`, `DELETE /api/images`, `POST /api/images`, `GET /api/manifest`, `GET /api/join-qr.svg` | `?event=`; absent means the default event |
| `POST /internal/retention` | New, see below |

## Screens

- `/show?event=<id>`. No parameter means the default event, so the church
  screen's current URL keeps working.
- The manifest and its ETag are built per event.
- The invite QR uses that event's join code.
- A closed event's screen keeps showing its approved images, with the invite
  hidden whatever `ShowJoinInvite` says.

## Retention sweep

Cloud Run scales to zero with CPU only during requests
([`infra/main.tf`](../../../infra/main.tf)), so an in-process timer cannot be
relied on.

- A Cloud Scheduler job, added in Terraform, calls `POST /internal/retention`
  once a day. It authenticates with a secret header, stored in Secret Manager
  and checked in constant time like the webhook secret.
- Admin Settings gets a "Rydd nå" button for the selected event; `/dev` gets the
  same.
- For each event with `MaxAgeDays` set:
  1. Keep: images received within `MaxAgeDays`, the `KeepNewest` newest approved
     images, pinned images, and the current takeover image.
  2. Delete every other image, whatever its status.
  3. One state write removes the records; then the objects are deleted. A
     failure in between leaves orphaned bytes, never a manifest entry pointing at
     nothing.
- Logs the count per event. Also logs the size of `state.json` on every write,
  so the point where one file stops being enough is visible.

## Error handling

- Reaction updates, the listening notice and callback answers are best effort
  and logged on failure.
- Slugs are validated and unique; the API refuses to change one.
- Creating or patching an event validates `OpensAt < ClosesAt` when both are set.
- The default event cannot be closed, scheduled or deleted, and retention is the
  only way its images are removed automatically.

## Testing

In `tests/EventPhotoBot.Tests`:

- Migration from a pre-groups and a post-groups `state.json`, and loading a
  migrated file unchanged.
- Private-chat event resolution as a table: current open or closed, member of
  the default or not, other open memberships or not.
- `/start <code>` for an open, closed and unknown code; keyboard contents.
- Button taps: switch, closed event, not a member.
- Groups: routing, notice on each change, unrouted and closed ignored, supergroup
  migration keeping the event, first photo adding the membership.
- Dedup scoped to the event.
- Ban revoking across events; unban restoring memberships.
- Retention: age cut-off, the `KeepNewest` floor, pinned and takeover
  exemptions, state written before objects deleted.
- Reactions set on approve, reject, hide and delete.
- Manifest, settings and takeover scoped by `?event=`, defaulting to the
  default event.
- Event deletion clearing memberships, current events and group routes.

## Out of scope

- Accounts and roles; the shared admin password stays.
- More than one instance, workers, a database.
- Reactions in private chats.
- Guest web upload.
- Splitting `state.json` into several files.

## To verify during implementation

- The Bot API's allowed reaction emoji for bots, and that 👀 and 🔥 are in it.
- Whether a reaction change notifies the photo's author, and how.

## Changes made while planning

Decided after the design above was drafted, during the planning pass that split
it into tasks:

- **The bucket's 30-day lifecycle rule is removed** (`infra/main.tf`). It
  deleted image bytes on age alone, which contradicted "kept until an admin
  deletes the event" and would have left the default event's `KeepNewest` pool
  pointing at deleted files.
- **Reactions are not changed on bulk deletes** (delete-all, deleting an
  event, the retention sweep). They are changed on single decisions, single
  deletes, takeover approval and the ban cascade. Hundreds of
  `setMessageReaction` calls inside one request risk Telegram's rate limit and
  the request timeout.
- **Event endpoints are split by what they replace:** `PATCH /api/events/{id}`
  sets the name only; `PUT /api/events/{id}/schedule` and
  `PUT /api/events/{id}/retention` replace those groups of fields, so "clear
  this date" needs no sentinel values.
- **Group routing body:** `{"eventId": "<id>"}` routes, `{"eventId": ""}`
  un-routes, and a missing or null `eventId` is a 400, so a malformed body can
  never un-route a group.
- **A code for a scheduled event** gets the reply "{navn} har ikke startet
  ennå." The design above only covered closed events.
- **The QR for a scheduled event is served,** so it can be printed in advance.
  It is refused only once the event is closed.
- **An event name cannot be empty,** because the bot says it in every
  acknowledgement. The "event name can be cleared" behaviour from before this
  release goes away.
- **Cloud Run's request timeout rises from 120 s to 900 s,** so a ZIP export
  of a large event can finish. Firebase Hosting cuts a request at 60 s, so a
  large export must use the `run.app` URL instead. Documented in the runbook.
- **Export file names use UTC:** `{ReceivedAt:yyyyMMdd-HHmmss}Z-{id}.{ext}`.
  The container may not carry timezone data for Europe/Oslo.

## Changes made during implementation

- **The retention sweep's Cloud Scheduler job is created by both deploy
  paths** — `infra/deploy.ps1` and the GitHub Actions `deploy` workflow, not
  just one of them. The GitHub Actions path needed a role neither deploy path
  required before: `roles/cloudscheduler.admin`, added to the WIF deploy
  service account in `infra/wif/main.tf`. `infra/wif` needs to be re-applied
  once before the first Actions deploy of this release, on any project where
  it was applied before this role existed.
