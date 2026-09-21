# Sender model — design

Replaces the whitelist-as-gate with a three-status sender roster, a
deploy-time join code delivered by QR, and a banlist that revokes what a
sender already sent.

## The problem

The whitelist is a gate: [`UpdateHandler.HandleAsync`](../../../src/EventPhotoBot/Telegram/UpdateHandler.cs)
looks the sender up and, finding nothing, declines. Nothing is downloaded and
nothing reaches the queue. A separate `Trusted` flag on each whitelist entry,
combined with the global `AutoApproveTrusted` setting, then decides whether an
admitted sender's photos skip the approval queue.

That is the wrong shape for the event. What is wanted:

- **Pre-approved photographers** — a named few whose photos go straight to the
  screen.
- **Everyone else at the event** — photos accepted, held for an organiser to
  approve.
- **Nuisances** — blocked outright, and whatever they already sent pulled down.

Under the current model the second group cannot send at all. The pieces for the
first group already exist (`Trusted` + `AutoApproveTrusted`), but they sit
behind the gate rather than replacing it.

## Decisions taken

| Question | Decision |
|---|---|
| How open is ingest? | Open behind a join code, because a QR deep link makes joining one scan and one tap. |
| Where does the code live? | Secret Manager, set at deploy time, alongside the five existing secrets. |
| What does banning do to existing photos? | Rejects every image from that sender, in the same write. |
| Is a banned sender told? | No. Silent drop. |
| How are the lists modelled? | One roster with a status per sender, replacing two lists. |
| Is "unknown" a status? | No. It is the absence of a roster row, and nothing is stored for it. |

## Sender model

```csharp
public enum SenderStatus { Known, AutoApprove, Banned }

public sealed class Sender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public SenderStatus Status { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}
```

`Settings.Senders` (a `List<Sender>`) replaces `Settings.Whitelist` and
`Settings.SeenSenders`.

Removed outright:

- `WhitelistEntry` and its `Trusted` flag — membership at `AutoApprove` is the
  trust tier now.
- `Settings.AutoApproveTrusted` — a global toggle for a per-sender property
  that no longer exists.
- `Settings.PairingMode` and the `SeenSender` type — pairing existed to collect
  the whitelist before the event. The join code does that job, continuously,
  without an organiser watching a screen.

A sender absent from the roster has no representation. People who message the
bot without the code are **not** recorded: recording them would let anyone who
finds the bot handle grow the roster with writes nobody authorised.

`Name` is captured once, at redemption, and not refreshed on later messages —
updating it would mean a state write on every photo. Each `ImageRecord` already
carries its own `SenderName` snapshot for display in the queue.

## Ingest decision

One roster lookup at the top of `HandleAsync`, before anything else runs:

| Roster status | Behaviour |
|---|---|
| `Banned` | Return immediately. No reply, no download, no object written. |
| *absent* | `/start <code>` with a matching code → row created as `Known`, welcome sent. Anything else → the rate-limited decline, pointing at the QR. Nothing stored either way. |
| `Known` | Ingest. Image lands `Pending` in the approval queue. |
| `AutoApprove` | Ingest. Image lands `Approved` with `DecidedAt` set. |

### Invariants

**Redemption only ever creates a row.** It never modifies an existing one. A
pre-approved photographer who re-scans the QR out of curiosity must not be
silently demoted to `Known`. A banned sender who scans the QR must not be
readmitted — the `Banned` branch returns before redemption is even considered.

**The decline reply is throttled; redemption is not.** The existing
one-reply-per-minute-per-sender cooldown (`_lastUnlistedReplyAt`) keeps the bot
from being used as a reply relay by a stranger poking at it. It applies only to
the decline. Someone who is declined, scans the QR, and sends the code ten
seconds later is admitted at once and gets the welcome — being throttled out of
joining would be the worst possible moment to rate-limit somebody.

**Nothing is downloaded before the decision.** The status check precedes
`GetFilePathAsync`, so a banned or un-redeemed sender never causes a Telegram
download, an image decode, or an object write.

### `/start` handling

| Sender | Message | Result |
|---|---|---|
| absent | `/start <valid code>` | Row created as `Known`; welcome blurb. |
| absent | `/start`, `/start <wrong code>`, or anything else | Decline pointing at the QR (throttled). |
| `Known` or `AutoApprove` | `/start` with or without a code | Welcome blurb. No roster change. |
| `Banned` | anything | Silence. |

The decline names the QR on the event screen, so someone who found the bot
handle another way knows what to do and does not conclude the bot is broken.

## Join code

`JOIN_CODE` becomes the sixth required value in
[`AppConfig`](../../../src/EventPhotoBot/AppConfig.cs), trimmed like the rest,
and validated at startup against:

```
^[A-Za-z0-9_-]{1,64}$
```

That is Telegram's deep-link payload charset. A code containing a space or
punctuation produces a link that silently fails to carry the payload, so this
fails the deploy instead of shipping a QR nobody can use. The failure names the
offending characters.

Comparison uses the hash-then-`FixedTimeEquals` pattern that `SecretTokenMatches`
already applies to the webhook secret. This is the second endpoint a stranger
can reach, and a length-leaking comparison there is free to exploit.

Rotating the code means a redeploy. The runbook already marks redeploys as a
before/after-event operation, because re-registering the webhook drops whatever
Telegram is holding — so the runbook note for rotation points at
`gcloud run services update` for the mid-event case.

## QR code

`ITelegramClient` gains `GetMeAsync`, called once at startup to learn the bot's
username, giving the deep link:

```
https://t.me/<bot_username>?start=<JOIN_CODE>
```

Rendered server-side as SVG by **QRCoder** (MIT; its SVG renderer has no
`System.Drawing` dependency, which matters for the Linux container) and served
at `GET /api/join-qr.svg`, behind the session gate like every other surface.

**Degradation:** if `getMe` fails at startup the service still comes up. The
username is cached as null, the QR endpoint returns 404, and the slideshow omits
the QR. A failed vanity call must not cost the event its screen. This is
deliberately unlike the missing-config case, which is fatal — a missing secret
means the service cannot work at all, whereas a missing QR means one affordance
is absent.

The slideshow renders the QR large in the empty state (the unbuilt line of
[`telegram-online-bot-spec.md:175`](../../../telegram-online-bot-spec.md)) and
as a small fixed corner badge whenever photos are showing, so people arriving
late can still join.

`SettingsView` in the manifest gains `joinUrl` (nullable), so the slideshow can
render the handle as text beside the QR without a second call.

## Banning

`POST /api/senders/{id}/status` with a target status. One `MutateAsync`, so the
whole effect is a single state generation:

1. Set the sender's status, creating the row if the admin is banning someone
   who has not redeemed.
2. When the new status is `Banned`: set every `ImageRecord` whose `SenderId`
   matches to `Rejected` with `DecidedAt = now`, and clear takeover if any of
   those images held it.

Step 2 reuses the existing `ClearTakeoverIfHeldBy` helper, which already backs
the same invariant on the single-image status endpoint.

This is destructive and not undoable from the UI: un-banning restores the
sender's ability to send but does not resurrect the rejected images. The admin
UI says so at the point of action rather than in a tooltip.

## API changes

`GET /api/settings` — `whitelist`, `seenSenders`, `pairingMode` and
`autoApproveTrusted` are removed; `senders` is added.

`PATCH /api/settings` — `whitelist` and `pairingMode` are removed and **nothing
replaces them**. The roster is deliberately not writable through the settings
patch: an array replacement cannot carry the ban cascade, so allowing it would
create a second write path that silently skips revoking a banned sender's
photos.

`POST /api/senders/{id}/status` — new, and the only way to change the roster.
Body `{ "status": "known" | "autoApprove" | "banned" }`. Rejects an unparseable
status with 400, mirroring the image-status endpoint. Creates the row if the id
is absent, which is how an organiser pre-approves a photographer (or pre-bans a
known nuisance) by typing an id before that person has ever messaged the bot.

There is no delete. The three statuses cover every intent an organiser has —
demoting a photographer is `Known`, stopping someone is `Banned` — and a delete
would differ from `Known` only in forcing a re-scan.

`GET /api/join-qr.svg` — new. Returns `image/svg+xml`, or 404 when the bot
username is unknown.

`GET /api/manifest` — unchanged except for `settings.joinUrl`. The endpoint
still performs no object-store I/O; the existing
`A_poll_performs_no_object_store_io` test continues to guard that.

## Admin UI

**Settings** loses the pairing banner, the pairing toggle, the auto-approve
toggle and the two tables, and gains one roster table: name, id, status control,
first seen. Adding someone by id still works, defaulting to `AutoApprove` —
adding a person by hand before the event is how a photographer gets
pre-approved. Every control on this table calls the status endpoint; the page no
longer PATCHes a roster array.

**Queue** gains a per-image control to set that image's sender to `Banned`,
because noticing a bad sender happens while looking at their photo, not while
reading a settings page.

## Slideshow

Empty state: existing copy plus the QR and the bot handle.

Non-empty: a small corner badge with the QR. Sized so it reads from across a
room without competing with the photo — this is a layout judgement to make
against the real screen, and the runbook's existing "run the slideshow for an
hour on the actual display machine" check is where it gets validated.

## Infrastructure

- `infra/main.tf` — a sixth entry in `local.secret_ids`, `eventphoto-join-code`,
  and its `secretEnvironmentVariables` entry on the Cloud Run service. The
  secret resource and its IAM binding both `for_each` over that map, so they
  need no other change.
- `infra/deploy.ps1` — prints the `gcloud secrets versions add` line for the new
  secret alongside the existing five.
- `.github/workflows/deploy.yml` — **no change.** Its bootstrap apply targets
  `google_secret_manager_secret.secrets` as a whole, which already covers every
  member of the `for_each`.

No new GitHub variables or secrets: the join code is secret material and stays
in Secret Manager, consistent with the existing rule that no secret value is
ever a Terraform variable or a GitHub secret.

## Testing

Handler behaviour:

- A `Banned` sender's photo: no reply sent, no object written, no image recorded.
- `/start <valid code>` from an absent sender: row created as `Known`, welcome
  sent.
- `/start <wrong code>` and a bare photo from an absent sender: decline sent,
  nothing stored.
- Second decline within the cooldown: no second reply.
- Decline followed by redemption inside the cooldown: admitted, welcome sent.
- `Known` sender's photo lands `Pending`.
- `AutoApprove` sender's photo lands `Approved` with `DecidedAt` set.
- `/start <code>` from an `AutoApprove` sender leaves the status unchanged.
- `/start <code>` from a `Banned` sender leaves the status unchanged and sends
  nothing.

Config:

- `JOIN_CODE` missing → `AppConfig.Load` throws, naming it.
- `JOIN_CODE` containing a space, or over 64 characters → throws, naming the
  constraint.
- A code with a trailing newline (the `--data-file=-` hazard the runbook warns
  about) is trimmed and accepted.

Ban cascade:

- Banning a sender rejects their pending and approved images in one generation.
- Banning the sender of the takeover image clears takeover in the same write.
- Banning a sender with no images succeeds and creates the row.
- `PATCH /api/settings` carrying a roster array does not change the roster.

Endpoints:

- `/api/join-qr.svg` without a session returns 401 or redirects.
- `/api/join-qr.svg` returns SVG when the username is known, 404 when it is not.

The existing `FakeTelegramClient` grows a `GetMeAsync` returning a fixed
username, plus a mode that fails, for the degradation test.

## Documentation

- **RUNBOOK** — "Collecting the whitelist (pairing mode)" is replaced by "Who
  can send", describing the three statuses and the QR. The pre-event checklist
  gains the join-code secret and a check that the QR is readable from the back
  of the room. The acceptance criteria are rewritten around the four cases,
  replacing the two pairing-mode criteria and the whitelist-rejection criterion.
- **Diagrams** — all three under `docs/diagrams/` currently show the gate model
  and become wrong the moment this ships. All three are regenerated, and a
  fourth is added showing the sender states and the transitions between them.

## Breaking changes

`state.json` changes shape with no migration path. An existing file's
`whitelist` and `seenSenders` are not read; the roster starts empty.

This is acceptable because the system is destroy-and-redeploy per event and
nothing is meant to carry over — but deploying this **mid-event** would drop
the roster, so every participant would have to re-scan. The runbook says so
plainly next to the existing mid-event deploy warning.

## Non-goals

- Migrating an existing `state.json`.
- Recording senders who message without the code.
- Un-banning restoring rejected images.
- Rotating the join code without a redeploy.
- Per-sender upload caps. The join code plus the banlist is the control; a cap
  is a reasonable later addition if one event proves it necessary.
