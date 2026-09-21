# Beyond one event

This app is built to be deployed for one event and destroyed afterwards. The
spec says so, and most of its design follows from it: one `state.json` in a
bucket, one Cloud Run instance, one shared password, one bot, one join code
baked into a secret. That is the right shape for what it does today.

This document is the other branch. If the thing should survive its first
event and be used again — by the same people for the next party, or by other
organisers for their own — this is what would have to change and what could
then be built. It is a sketch of direction, not a plan: nothing here is
scheduled, sized in days, or committed to. Effort notes are judgement from
reading the code, not measurement.

## What has to change first

Four things underpin nearly everything else. None of them is a feature anyone
would notice, and every feature below waits on at least one of them.

### 1. State in a database, not a JSON object

Today the whole event lives in one `state.json`, loaded once at startup, held
in memory, and rewritten in full on every change with a generation
precondition. At tens of senders and low hundreds of images that is not just
adequate, it is why the manifest poll can do zero object-store I/O.

It stops working when events accumulate. Rewriting one object per change turns
into rewriting the archive per change, a cold start turns into loading every
event ever run, and two events being moderated at once contend on a single
lock. A permanent app needs per-event rows, an index that can answer "pending
photos for event X" without scanning everything, and migrations — which this
codebase deliberately has none of, because there was nothing to migrate to.

### 2. More than one instance

`max_instances = 1` is load-bearing in places that do not announce it. The
album-acknowledgement set and the unlisted-reply throttle in `UpdateHandler`
are in-process fields behind in-process locks; the duplicate check is a scan of
the in-memory dictionary under the state store's semaphore; the 50-megapixel
decode cap exists because an OOM on the single instance takes the slideshow
down with it.

Running two instances means the dedup guarantee moves into the database as a
unique constraint on `(event, sha256)` and `(event, fileUniqueId)`, the
throttles move to shared storage or get accepted as approximate, and image
decoding moves off the webhook request onto a worker. That last one is worth
doing anyway: the bot currently downloads and decodes inside the request
Telegram is waiting on.

### 3. Events as a first-class thing

An event today is the deployment. Its name is the one per-event setting; its
join code is a deploy secret; its images are the bucket. Making events data
rather than infrastructure means an event id, a lifecycle (draft, open,
closed, archived), per-event settings, per-event senders, a bucket prefix, and
a join code that can be rotated without a redeploy — useful within a single
event too, when a code leaks to the wrong group chat.

### 4. Identity beyond one shared password

One password, one signed cookie, everyone behind it is the admin. That is
honest for an event where the organisers are three people in the same room. A
permanent app needs accounts, per-event membership, and at least three roles:
an owner who can create and delete events, moderators who can only decide on
photos, and a screen account that can open `/show` and nothing else — a
projector left logged in should not be a door into the queue.

Once decisions have an author, they can be recorded. A moderation audit — who
approved what, who banned whom — is cheap to add at that point and impossible
before it.

## What could then be built

### Getting photos in

| Feature | Notes |
| --- | --- |
| Guest web upload | `/upload` exists but sits behind the shared password, so it is an organiser tool. A guest-facing version needs a per-guest token from the join link, its own rate limit, and a way to hand the token back after a browser restart |
| Own-submissions view | A guest seeing what they sent, and withdrawing something they regret before it is decided, removes most "can you delete that one" messages to the organiser |
| HEIC | Declined today with instructions for the sender, which works but costs every iPhone user one failed attempt. A decoder on the server ends the class of problem |
| Video | Declined outright. Real support means transcoding, a job queue, size and duration limits, and a wall that can play without audio. Depends on workers existing |
| Other channels | WhatsApp or a plain email address as alternative ingests. The roster and approval queue do not care where a photo arrived from |

### Moderating at volume

Approving a few hundred photos one at a time is fine. Thousands is not.

- Bulk select and decide, with an undo window rather than a confirmation
  dialog
- Keyboard shortcuts on the queue — next, previous, approve, reject, undo —
  so a laptop moderator never touches the mouse
- A phone-shaped review surface, since the person moderating is usually
  standing up
- Filters and sort beyond the current status tabs: by sender, by time, by
  whether a face is in frame
- Trust that accrues: a sender whose first N photos were all approved stops
  being queued, which is the auto-approve photographer role granted
  automatically instead of by hand

### The screen

The slideshow is the part of this app that is already good. Takeover, the
recurring pin, the seeded shuffle, Ken Burns, and the invite toggle are all
worth keeping exactly as they are. What is missing is variety and reach.

- More layouts than one full-bleed image: a mosaic for when photos arrive
  faster than eight seconds apart, a filmstrip, a split for portrait pairs
- Per-event look — accent colour, typeface, frame — with contrast checked
  server-side rather than trusted to whoever picked the colour
- Overlays that are not photos: the programme, a countdown, a message from
  the host, all currently faked by uploading an image and pinning it
- Several screens on one event, each with its own playlist — a foyer screen
  with the invite hidden and a main-room screen with it shown, which the
  current single `ShowJoinInvite` setting cannot express
- Push instead of poll. Two-second polling with an ETag is cheap and survives
  a flaky venue network better than a socket does; this is worth doing only
  if the poll cost actually shows up across many concurrent events

### After the event

This is where a permanent app differs most from a throwaway, because right now
the answer to all of it is "pull from the bucket, then `terraform destroy`".

- Album export as a ZIP of originals, which is the single most requested
  thing after any event
- A guest gallery link — read-only, no password, scoped to one event and
  revocable
- Retention: a per-event policy with an automatic purge, so photos are not
  kept by default forever because nobody remembered to delete them
- Backup and restore with checksums, verified before an event rather than
  discovered during one
- Erasure on request: a guest asking for their photos to be removed should be
  a button, not a support conversation. Keeping personal data past the night
  is what makes this obligatory rather than nice

### Operating it

- Self-service event creation, which is the whole point of the change: an
  organiser should not run Terraform to hold a party
- Per-event metrics — photos in, queue depth, decision latency — and an alert
  when the queue stops being drained mid-event
- CI that runs on push rather than only by hand, staged environments, and a
  migration story
- Serving image bytes through signed URLs or a CDN instead of proxying every
  one through the app, which the current password gate requires
- Rate limits per sender as well as per IP

### Language

Every string the bot and the pages produce is Norwegian, written inline. One
event, one room, one language is the right call today. A product used by
anyone else needs the strings extracted and the guest-facing ones chosen from
what the sender's client reports — organiser-facing text can stay in one
language far longer than guest-facing text can.

## What should not be built

Worth writing down, because these all look reasonable on a feature list:

- **Comments, likes, a feed.** The screen is the output. Turning it into a
  social product invites moderation problems an event organiser has no
  appetite for.
- **Face recognition or auto-tagging.** Biometric processing of guests who
  came to a party and sent a photo is not a trade anyone here should make.
- **Guest accounts.** A join link and a device-scoped token do the job. An
  account is a password to forget and a record to keep.
- **Automatic quality filtering.** Rejecting blurry photos by algorithm will
  reject the one shot someone cared about. Moderation is a human decision.

## A plausible order

1. Database, workers, and events as data. Nothing user-visible ships in this
   phase; it is the whole cost of leaving the throwaway design behind.
2. Accounts, roles, and audit. Then album export and retention, which are the
   two things an event organiser notices immediately.
3. Guest web upload and the own-submissions view, which together unlock the
   people who will not install Telegram.
4. Wall layouts, theming, and multiple screens.
5. Video, if the demand is real after the first four.

The honest alternative is to not do any of it. The current design is small
enough to read in an afternoon and cheap enough to run that deploying a fresh
copy per event costs less than maintaining a platform. That remains a
legitimate answer, and the fact that it stays legitimate is worth re-checking
before phase 1 rather than after it.
