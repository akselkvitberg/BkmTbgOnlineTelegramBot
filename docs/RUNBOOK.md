# Event photo bot — runbook

This is what to follow on the day, and the checks that prove the system is
actually working before you rely on it. The whole system is designed to be
destroyed after the event — see **After** — so treat teardown as a step on
this list, not a someday task.

Commands below that name a resource (`eventphoto-bot-token`, the Cloud Run
service `eventphoto`, and so on) assume the default `-Name eventphoto`. If
this event was deployed with a different `-Name`, substitute it everywhere a
resource name appears.

## Before the event

- [ ] Bot created via BotFather, token stored using the safe non-interactive form —
      typing a value and pressing Enter at an interactive `--data-file=-` prompt stores
      a trailing newline in the secret, which AppConfig now trims, but avoiding it is
      one less thing to debug:
      `printf '%s' 'YOUR_TOKEN' | gcloud secrets versions add eventphoto-bot-token --data-file=- --project PROJECT_ID`,
      `/setuserpic` and description set so it looks deliberate
- [ ] `RETENTION_SECRET` stored (required — Cloud Run refuses to start the
      revision if this secret has no version at all):
      `openssl rand -hex 32 | gcloud secrets versions add eventphoto-retention-secret --data-file=- --project PROJECT_ID`.
      See **Events and retention** below for what it's for.
- [ ] Join code chosen and stored — the secret still needs a version for
      Cloud Run to start even if you don't care what it is:
      `printf '%s' 'YOUR_CODE' | gcloud secrets versions add eventphoto-join-code --data-file=- --project PROJECT_ID`
      — letters, digits, `_` and `-` only, at most 64 characters. Anything else
      fails startup, on purpose: Telegram silently drops a deep-link payload
      outside that set, so a code with a space would ship a QR nobody can use.
      Its *value* is optional: `JOIN_CODE` only seeds the default event's
      ("Daglig") join code on the very first start after a fresh deploy; leave
      it blank (an empty version) and Daglig gets a generated code instead. A
      fresh event created later in `/admin/events` always gets a generated
      code regardless of `JOIN_CODE`.
- [ ] Event name typed into admin settings under "Navn og visning". It is only
      shown on the slideshow while no photos have arrived yet, and it is a
      setting rather than a deploy value, so fixing a typo mid-event costs
      nothing and needs no deploy. This sets the name of the currently
      selected event; a wedding or another special event alongside the daily
      screen is created separately in `/admin/events` (see **Events and
      retention** below)
- [ ] Pre-approved photographers added by Telegram id and set to Auto-approve
      (see "Who can send" below)
- [ ] If photos should be collected from a Telegram group: privacy mode turned
      off in BotFather **before** the bot is added to the group, then the group
      routed to the event under Telegram → Grupper in admin (see "Collecting
      from a group" below)
- [ ] QR on the slideshow checked from the back of the room, on the actual
      display machine — a QR nobody can scan makes the whole join flow useless.
      A screen the wrong public can see — a foyer, a street-facing window —
      should instead have "Vis QR-kode og invitasjon" turned off on the settings
      page, which hides the QR, the bot handle and the wording that asks for
      photos. That is a change to what the screen shows, not to who may send:
      anyone already holding the join link keeps it, so a screen that must
      genuinely stop accepting photos needs the senders list, not this toggle.
- [ ] Programme and menu images uploaded via `/upload` and pinned as
      recurring from the images page
- [ ] Takeover set and cleared once, so whoever runs the screen has done it
      before they need to under pressure
- [ ] Full path tested from a real phone: send a photo, it appears in the
      queue, approve it, it appears on the slideshow
- [ ] Portrait photo from both an iPhone and an Android checked for
      orientation
- [ ] Slideshow run for an hour on the actual display machine, to catch sleep
      and memory issues before the event does
- [ ] Friendly URL claimed, if one is wanted — set `HOSTING_SITE` and deploy
      once. The `.web.app` name is globally unique and first-come, so a name
      chosen on the day may already be gone (see "The friendly URL" below)
- [ ] Login link and password shared with whoever will run the screen
- [ ] Display machine's browser open on `/show`, fullscreen, OS-level sleep
      disabled (not just the browser tab)
- [ ] Billing budget alert set on the project by hand. `infra/main.tf`'s
      `google_billing_budget` resource is commented out (it needs a billing
      account id and the billingbudgets API enabled, which Terraform can't
      assume it has permission to touch) — create the equivalent budget
      alert yourself in the Cloud Console under Billing → Budgets & alerts
      before the event starts, not after.
- [ ] Scheduled teardown booked. Put "run the After checklist" (below) on a
      calendar or reminder for the day after the event — the whole point of
      destroying everything is that it happens, and a manual step with no
      reminder is the one that gets forgotten.

### Who can send

Every person the bot knows about can be **Banned** — globally, across every
event — from the Telegram page in admin or from the approval queue. Banning
drops their messages silently, and **rejects every photo they already sent in
the same action**, including any already on screen. This cannot be undone
from the UI: un-banning restores their ability to send but does not bring the
photos back.

Anyone not banned has, for each event they are a member of, one of two
statuses, set per event on the Telegram page:

- **Review first** — the default for anyone who joins that event by scanning
  its QR. Their photos to that event land in the approval queue.
- **Auto-approve** — pre-approved photographers for that event. Their photos
  to that event go straight to the screen with no queue step. Set this before
  the event by adding their Telegram id under that event, or promote them
  once they have scanned.

Someone who has never scanned any QR is not on the list at all. Their photos
are declined with a message pointing at the screen, at most one reply per
minute, and nothing is downloaded or stored. Status changes take effect on
that person's very next message — no redeploy needed.

People join an event by scanning its QR, shown on that event's slideshow or
printed from `/admin/events`. It encodes `https://t.me/<bot>?start=<join
code>`; scanning opens the bot chat with a Start button, and tapping it sends
the code. That is the whole flow — one scan, one tap.

Each event's join code is generated by the app (or, for Daglig only, seeded
from the `JOIN_CODE` secret on first start — see **Before the event**).
Rotating a code — "Ny QR-kode" in `/admin/events` — is an admin action, not a
deploy: the old code and printed QR stop working immediately, and anyone
already a member keeps sending. A join code is not a password: everyone in
the room can see the QR, and so can anyone shown a photo of the screen. It
stops someone who merely guesses the bot handle, nothing more. The banlist is
what handles a person you actually want out.

### Collecting from a group

The bot can also pick up photos members post in a Telegram group — the event's
own group chat, say — alongside the private chats above.

1. **Turn privacy mode off, first.** With privacy mode on (Telegram's default) a
   bot in a group sees only commands and replies, never the photos. In Telegram,
   message @BotFather, send `/setprivacy`, pick the bot and choose **Disable**.
   Telegram applies this only to groups the bot joins *afterwards*, so if the bot
   is already in the group, remove it and add it again. Making the bot an admin of
   the group also lets it see everything, if you would rather not change the
   setting. Telegram → Grupper in admin shows a red warning while privacy mode is
   still on; "Sjekk igjen" re-asks Telegram after you change it.
2. **Add the bot to the group** from the group's member list. It then appears
   under Grupper on the Telegram page, routed to "Ikke koblet". While unrouted,
   the bot ignores the group completely: no replies, nothing stored.
3. **Route the group to an event**, in the dropdown next to the group on
   Telegram → Grupper — Daglig or any other open or scheduled event. The bot then
   posts one message in the group saying that photos posted there from now on
   may be shown on the screen for that event, with the poster's name, after an
   organiser approves them. That message is the only text the bot ever writes in
   a group. Posting a join code in the group does nothing; routing only happens
   from admin.

From then on, each photo a member posts goes through the same path as a private
send to that event — duplicates dropped, banned members ignored, Review first or
Auto-approve by the member's status for that event. A member who is not on the
list yet is added as Review first on their first photo. There are no text
replies in the group; instead the bot reacts to each photo it took: 👀 queued,
🔥 straight on screen. Anything it could not use (a video, a HEIC file, a
download that failed) gets no reaction and no reply, so a member who needs a
photo shown should send it to the bot privately instead.

Photos posted as the group (anonymous admins) or by other bots are ignored —
they carry no person to approve or ban.

To stop collecting, route the group to "Ikke koblet", which keeps the bot in the
group, or use "Forlat gruppen", which makes the bot leave. Photos already taken
stay where they are either way; ban a member or reject photos in the queue to
remove them. If the event a group was routed to gets deleted, the group falls
back to "Ikke koblet" with no notice posted. If a group must stay closed, make
the bot leave it.

The bot keeps its own list of groups because Telegram offers no way to ask which
groups a bot is in, or which people have started it. The list is filled from the
notifications Telegram sends when the bot is added or removed, so a group the bot
joined before this version was deployed does not appear until it is removed and
added again — there is no other way to register it, since posting a join code in
a group no longer does anything. At most 20 groups that are not routed to an
event are remembered; the oldest drop off first, so a stranger adding the bot to
many groups cannot grow the state file without bound.

### Events and retention

One event runs by default: Daglig, the church's daily screen, always open and
never deleted. A special event — a wedding, a concert — is created alongside
it in `/admin/events`: name, a short id for its `/show?event=<id>` URL, and an
optional opens/closes time. It gets its own generated join code, QR and
screen, kept apart from Daglig's photos. Closing it (by its end time or
manually) stops it taking photos and hides its invite; its photos stay until
an admin deletes the event, which is the only way to remove them in bulk. A
group is routed to whichever event it should feed from Telegram → Grupper —
see **Collecting from a group** above.

**No bucket lifecycle rule.** Photos are kept until an organiser deletes them,
or an event's own retention setting does. There is nothing left in
`infra/main.tf` that deletes an object by age alone.

**Retention** is per event, set on the Innstillinger page for that event:
delete images older than N days, always keeping the newest K approved ones
(pinned images and the one holding takeover are never deleted). It is off by
default for every event, including Daglig after an upgrade — turn it on
deliberately. "Rydd nå" on that page runs the sweep for the selected event
immediately, without waiting for the schedule.

The scheduled sweep is a Cloud Scheduler job, `<name>-retention`, created by
both deploy paths and run daily at 03:15 Europe/Oslo against every event with
retention turned on. Its region is `-SchedulerRegion` for `deploy.ps1` or the
`SCHEDULER_REGION` repository variable for the Actions path (both default to
`europe-west1`, independent of `GCP_REGION`/`-Region`, since Cloud Scheduler
is not offered in every Cloud Run region). See **One-time setup** and
**Upgrading an existing deployment to this release** below for what has to
exist before this job can be created.

**Export.** Each event's approved originals can be downloaded as a ZIP from
`/admin/events` ("Last ned alle (ZIP)"). A large event's ZIP can take minutes to
stream — download it from the `run.app` URL, not the `.web.app` one: Firebase
Hosting cuts a request at 60 seconds, well under what a big export needs.

**Replacing a printed QR.** The join code baked into `JOIN_CODE` at first
deploy only ever seeded Daglig's code. Once a newly printed QR is up and
nobody needs the old one, rotate Daglig's code from `/admin/events` ("Ny
QR-kode") so the old code — and the old printed QR — stops working.

## During the event

The admin watches the approval queue on a phone. If nobody is free to
moderate, set everyone in the People list to Auto-approve — the join code and
the banlist are then the only controls left, which is a reasonable posture for
a room full of people you know. Be aware it applies to everyone already on the
list, not to people who scan later: new joiners still arrive as "Review first",
so the queue keeps filling unless you promote them too.

**If the screen freezes:** reload the page. The slideshow polls the manifest
every couple of seconds and picks up from there; nothing is lost by
reloading.

**If one image is stuck on screen (a forgotten takeover):** every admin page
shows a banner naming the image holding takeover, with a one-tap clear. A
takeover set to "until I clear it" is the most likely way to strand a photo
on the wall for the rest of the night — that banner is the fix.

**Do not merge anything to `master` while the event is running.** A push to
`master` deploys, and deploying re-registers the Telegram webhook, which
discards whatever Telegram is holding at that moment — a photo sent in that
window is gone, silently. If a fix genuinely cannot wait, roll it out with
`gcloud run services update` by hand instead, and merge afterwards.

**Caption overlay on the slideshow** is a local, per-machine preference, not
a server setting: press `c` on the display machine's keyboard to toggle it.
It does not sync to any other viewer of `/show`, and does not survive
opening `/show` on a different machine.

## After the event

- [ ] Download the photos if anyone wants them, before destroying the bucket:
      `gcloud storage cp -r gs://BUCKET_NAME/originals ./photos`
      (`BUCKET_NAME` is the Terraform `bucket_name` output, or run
      `terraform -chdir=infra output -raw bucket_name`)
- [ ] Tell the group the photos are being deleted, if you haven't already —
      the bot's `/start` reply already promises this, so people are expecting it
- [ ] `deleteWebhook` on the bot (`https://api.telegram.org/bot<token>/deleteWebhook`),
      then delete the bot itself via BotFather
- [ ] Run the **destroy** workflow from the Actions tab (type the project id
      to confirm) — leaves no bucket, secret or service behind. From a
      workstation instead: `terraform -chdir=infra destroy`, against the same
      shared state
- [ ] Confirm the bucket is gone in the console or with
      `gcloud storage buckets list --project PROJECT_ID`, not just that it
      looks empty — an emptied bucket is not a destroyed one, and the whole
      point of the exercise is that nobody's photos are sitting in a bucket
      after the event ended
- [ ] Purge Cloud Logging. Logs are not a Terraform resource, so
      `terraform destroy` leaves them behind — and they can carry sender
      names, captions and (until this fix) even the bot token from earlier
      builds. From the project's Logs Explorer, delete the retained log
      buckets (or run `gcloud logging buckets list --project PROJECT_ID` and
      delete each one), rather than assuming project deletion alone clears
      them on your timeline
- [ ] Delete the retention Cloud Scheduler job. Like the webhook
      registration, it is created with `gcloud`, not Terraform, so
      `terraform destroy` leaves it behind:
      `gcloud scheduler jobs delete eventphoto-retention --location <scheduler region> --project PROJECT_ID`

If the event repeats (a second party, a second Sunday), destroying and
redeploying from scratch is the intended pattern — there is no state that
carries over on purpose, and rerunning the **deploy** workflow takes
minutes. The one-time setup (state bucket, WIF, repository variables) stays
in place between events; only the secret versions have to be added again
after a destroy, since destroying the secret resources takes their versions
with them.

## Acceptance criteria

These are checks a person runs against the deployed service. Each one is
written so it has an unambiguous pass or fail — do them in order, since
several depend on state left by the one before.

- [ ] **Scanning the QR admits a new sender.** From a phone that has never
      messaged the bot, scan the QR on the screen and tap Start. Pass: the bot
      replies "You are in", and the phone appears under Personer on the
      Telegram page in admin with status "Review first".
- [ ] **A newly admitted photo reaches the queue within five seconds.** Send
      one photo from that phone and time it with a stopwatch from send to
      appearance in `/admin/queue`. Pass: under five seconds. If you send an
      album of five and it's slower, that's expected — Telegram delivers album
      members as separate updates in series, not the bot being slow.
- [ ] **An auto-approve sender skips the queue.** Set that phone to
      Auto-approve in settings, then send another photo *without redeploying
      anything*. Pass: it reaches the slideshow with no approval step.
- [ ] **Someone who has not scanned is declined and nothing is stored.** From a
      second phone, message the bot directly without scanning. Pass: the reply
      points at the QR, and `gcloud storage ls -r gs://BUCKET_NAME/originals`
      (before and after, compared) shows no new object.
- [ ] **A special event's photos stay off the daily screen.** Create an event
      in `/admin/events`, scan its QR from a phone and send a photo, then
      approve it. Pass: the photo appears on `/show?event=<id>` for that
      event, and not on `/show` (Daglig).
- [ ] **Closing an event falls a daily member back to Daglig.** Close that
      event, then from a phone that is also a Daglig member (or has only ever
      scanned Daglig's QR) send a photo. Pass: it is accepted, and the bot's
      acknowledgement reads "Mottatt til Daglig — …", not the closed event's
      name.
- [ ] **A group is ignored until it is routed.** With privacy mode off, add
      the bot to a test group and post a photo there. Pass: the group appears
      under Grupper on the Telegram page routed to "Ikke koblet", the bot says
      nothing in the group, and no new object appears in `originals/`.
- [ ] **Routing a group posts one notice and collects photos.** On the
      Telegram page, route the test group to an event. Pass: the bot posts the
      notice once; a photo posted afterwards by a member who has never
      messaged the bot gets a 👀 reaction, lands in the queue, and that member
      appears under Personer as "Review first". The bot writes no other text
      in the group.
- [ ] **Approving a group photo turns 👀 into 🔥.** Approve that photo from
      the queue. Pass: the bot's reaction on the original group message
      changes from 👀 to 🔥, within a couple of seconds.
- [ ] **Leaving a group works from admin.** Press "Forlat gruppen" for the
      test group. Pass: the bot is gone from the group's member list and the
      row disappears from the Telegram page.
- [ ] **Banning revokes what a sender already sent.** With one approved photo
      from a test phone in the rotation, ban that sender from its queue card.
      Pass: the photo leaves the rotation within two seconds, and a further
      photo from that phone produces no reply at all and no new object.
- [ ] **Approving an image updates an open slideshow live.** Have `/show`
      open in a browser tab already, approve a pending image from another
      device. Pass: it appears in the rotation within two seconds, with no
      manual reload of the slideshow tab.
- [ ] **Hiding or deleting an image removes it live.** With `/show` still
      open, hide or delete an image that is currently in rotation. Pass: it
      stops appearing within two seconds, no reload.
- [ ] **Steady-state manifest polls are free.** Open the browser's network
      tab on `/show` while nothing is changing. Pass: `GET /api/manifest`
      requests return `304 Not Modified`. This does not depend on eyeballing
      Cloud Run's logs — the app logs no GCS calls at any level, so absence
      of a log line there would pass whether or not a call happened. The
      claim that the manifest is served from the in-memory generation
      counter rather than a bucket read is proven instead by the unit test
      `A_poll_performs_no_object_store_io`, which fails the build if that
      ever regresses.
- [ ] **Takeover displaces the slideshow within one slide.** Set takeover on
      an approved image from an admin page. Pass: the currently open
      slideshow switches to that image on its next poll (at most one slide
      interval later), and switches back to the rotation once takeover is
      cleared.
- [ ] **A second takeover replaces the first.** With takeover already set on
      image A, set it on image B without clearing A first. Pass: only B is
      shown; checking `/api/settings` shows a single `takeoverImageId`, not
      both.
- [ ] **Deleting or hiding the takeover image clears takeover in the same
      write.** With an image holding takeover, delete it (or set it to
      hidden). Pass: the takeover banner disappears immediately and
      `/api/settings` shows `takeoverImageId: null` — no separate step
      needed to clear it.
- [ ] **A recurring pin appears on schedule.** Pin an image as recurring,
      note the configured interval in settings (`recurringEvery`), and watch
      the slideshow for that many slides. Pass: the pinned image appears
      once per that many slides.
- [ ] **Every page and image requires a session.** Open a private/incognito
      window with no cookies and request each of `/show`, `/admin/queue`,
      `/admin/images`, `/admin/settings`, `/upload`, and an image URL under
      `/img/...` directly. Pass: each one returns `401` or redirects to login
      — never the actual content.
- [ ] **Portrait orientation is correct from both platforms.** Send one
      portrait photo from an iPhone and one from an Android phone. Pass:
      both display upright on the slideshow, not rotated or letterboxed
      sideways.
- [ ] **Multi-select upload works from a real iPhone.** Open `/upload` on an
      iPhone over the `.web.app` URL (not `run.app` — Firebase Hosting fronts
      the service and has its own request limits), pick five or six photos in
      one go from Fotobibliotek, and upload. Pass: all of them arrive and show
      on the slideshow. The one to watch for is HEIC — Safari normally hands
      over a JPEG from the library picker, but a photo reached through the
      Files app can arrive as HEIC, which is declined with a message saying so.
      If HEIC turns out to come through the library picker too, server-side
      HEIC decoding becomes a real requirement rather than the edge case it is
      treated as now.
- [ ] **Killing the instance mid-event loses nothing.** While the slideshow
      is running and polling, force the old instance to be replaced with
      `gcloud run services update eventphoto --project PROJECT_ID --region
      europe-north1` (with no real change). Pass: the slideshow keeps going
      from its next poll with no images or approvals lost — state is
      reloaded from `state.json` at the new instance's startup. Do **not**
      use `infra/deploy.ps1 -SkipBuild` for this check while the event is
      live, and do not run the **deploy** workflow either: both re-register
      the webhook with `drop_pending_updates: true`,
      which discards anything Telegram is holding for the webhook at that
      moment — a photo sent in that window vanishes with no error to the
      sender or the admin. `deploy.ps1` is for before/after the event, or a
      scratch project; `gcloud run services update` is the mid-event tool.

      A deploy that changes the `state.json` shape carries a second mid-event
      hazard on top of that one: the People list lives in `state.json` and
      there is no migration, so everyone would have to re-scan the QR. The
      sender-roster release (three statuses, join code, banlist) is exactly
      such a change — fine before an event, unacceptable during one.
- [ ] **`terraform destroy` leaves nothing behind.** Normally this is the
      **destroy** workflow from the Actions tab. To check it from a
      workstation instead: from a clean clone `infra/`'s own state has never
      been initialized in this checkout, so run `terraform -chdir=infra init
      -backend-config="bucket=<state bucket from One-time setup>"` first (see
      **One-time setup** above), then `terraform -chdir=infra destroy` (in a
      scratch project first if you want to check this without touching the
      real event's data). Pass: the
      bucket, all seven secrets, and the Cloud Run service are all gone
      afterwards — check with `gcloud storage buckets list`, `gcloud secrets
      list`, and `gcloud run services list`, all scoped `--project
      PROJECT_ID`.

## One-time setup (before the first deploy, either path)

`infra/deploy.ps1` and the GitHub Actions workflows apply the same `infra/`
module to the same project, so they have to read and write the same
Terraform state — otherwise each path can believe it owns resources the
other created, an apply from one side tries to recreate what the other
already made, and `terraform destroy` from either one stops being able to
see what the other left behind. For a system whose whole teardown story
rests on "destroy leaves nothing behind", two divergent states is exactly
the failure mode to design out. This is why `infra/main.tf` points at a
shared GCS backend instead of each path keeping its own local state, and it
holds **regardless of which path you use to deploy** — a local apply
sharing state with CI is deliberate, not a quirk of the GitHub Actions setup.

Do the following once per GCP project, not per event.

**0. Enable the Cloud Resource Manager API.** Do this first, before any
Terraform runs:

```bash
gcloud services enable cloudresourcemanager.googleapis.com --project PROJECT_ID
```

Every Terraform config here manages `google_project_service` resources, and
the provider calls Resource Manager to do it. A workstation apply can get
away without this — user credentials bill their API quota somewhere else, so
the call succeeds — which makes the gap invisible until the first GitHub
Actions run: the deploy service account's calls bill the target project, and
Terraform fails at the bootstrap apply with `Error 403 ... SERVICE_DISABLED`
naming `cloudresourcemanager.googleapis.com`, not naming the resource you
were actually trying to create. The app's own APIs (run, artifactregistry,
secretmanager, storage, iamcredentials) do *not* need enabling by hand —
`infra/main.tf` turns those on itself.

**1. State bucket (`infra/backend/`)** — required before the *first* deploy
from either path, including the very first `deploy.ps1` run. Creates the GCS
bucket `infra/main.tf`'s backend block points at. Uses its own local state
(never migrated anywhere) and is applied by hand from a workstation with
`gcloud` already authenticated against the project.

```bash
cd infra/backend
terraform init
terraform apply -var "project_id=PROJECT_ID"
terraform output -raw bucket_name    # -> the -StateBucket / TF_STATE_BUCKET value used below
```

**2. Workload Identity Federation (`infra/wif/`)** — required for the GitHub
Actions path, which is how this project is deployed; skip it only if you
intend to deploy exclusively from a workstation. Creates the pool, provider
(locked to this one GitHub repository) and the service account the workflows
act as, and grants that service account the roles it needs to run Terraform
against `infra/` and to push images to Artifact Registry. Each role is
justified in a comment next to it in `infra/wif/main.tf`.

Confirm the exact `OWNER/REPO` value before applying — the provider's
attribute condition compares it byte for byte against the `repository`
claim GitHub's OIDC token carries, which is the account's own canonical
casing, not necessarily what a clone URL or a habit of typing the name
happens to show. A mismatch doesn't fail loudly at apply time; it fails
closed later, at the workflow's authentication step, with an opaque STS
error that doesn't mention casing at all. Check it first:

```bash
gh api repos/OWNER/REPO --jq .full_name
```

```bash
cd infra/wif
terraform init
terraform apply -var "project_id=PROJECT_ID" -var "repository=OWNER/REPO"
terraform output -raw workload_identity_provider    # -> GCP_WORKLOAD_IDENTITY_PROVIDER
terraform output -raw deploy_service_account_email  # -> GCP_DEPLOY_SERVICE_ACCOUNT
```

**3. If `infra/` already has local state from before this setup existed** —
the next `terraform init` there (from either path) will notice the backend
block changed and offer to migrate that local state into the new bucket.
Accept that once, from a workstation, so the state used up to now is not
orphaned:

```bash
cd infra
terraform init -backend-config="bucket=<bucket_name from step 1>"
```

A brand new project has no local state yet, so this step is a no-op the
first time through — `deploy.ps1` and the workflows both pass
`-backend-config`/`-StateBucket` themselves on every run regardless (see
below).

**4. GitHub repository variables** — required for the GitHub Actions path.
Under **Settings → Secrets and variables → Actions → Variables**, set:

| Variable | Value |
| --- | --- |
| `GCP_PROJECT_ID` | The project id (same one used above) |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | Output of step 2 |
| `GCP_DEPLOY_SERVICE_ACCOUNT` | Output of step 2 |
| `TF_STATE_BUCKET` | Output of step 1 |
| `GCP_REGION` | Optional — defaults to `europe-north1` if unset |
| `APP_NAME` | Optional — defaults to `eventphoto` if unset |
| `HOSTING_SITE` | Optional — the Firebase Hosting site id, e.g. `tbg-event-photos` for `https://tbg-event-photos.web.app`. Unset means no Hosting and the `run.app` URL as the only way in. See **The friendly URL** below |
| `SCHEDULER_REGION` | Optional — defaults to `europe-west1` if unset. Where the retention sweep's Cloud Scheduler job runs from; see **Events and retention** above |

None of these are secret — they're project ids, resource names and a bucket
name. No bot token, admin password or signing key is ever configured as a
GitHub secret or variable.

That secret material stays in Secret Manager, added by hand the same way
regardless of which deploy path is used — but not yet: the four steps above
create the state bucket and the WIF trust, not the app's own secret
*resources*. Those come from either path's own bootstrap step (`deploy.ps1`'s
targeted apply, or the `deploy` workflow's "Terraform bootstrap apply" step).

**5. The first deploy takes two runs, and the first one fails.** This is
expected, not a misconfiguration. Terraform creates the secret *resources*;
a secret resource with no version is not something Cloud Run can mount, and
the webhook step has no token to read. So the first `deploy` run gets as far
as creating the registry and the seven secrets, builds and pushes the image,
and then fails at "Terraform apply" or "Register the Telegram webhook".
Add the versions at that point, from a workstation authenticated against the
project:

```bash
printf '%s' 'YOUR_BOT_TOKEN'        | gcloud secrets versions add eventphoto-bot-token      --data-file=- --project PROJECT_ID
printf '%s' "$(openssl rand -hex 32)" | gcloud secrets versions add eventphoto-webhook-secret --data-file=- --project PROJECT_ID
printf '%s' "$(openssl rand -hex 32)" | gcloud secrets versions add eventphoto-webhook-path   --data-file=- --project PROJECT_ID
printf '%s' 'A_PASSWORD_YOU_CHOOSE' | gcloud secrets versions add eventphoto-admin-password --data-file=- --project PROJECT_ID
printf '%s' "$(openssl rand -hex 32)" | gcloud secrets versions add eventphoto-cookie-key     --data-file=- --project PROJECT_ID
printf '%s' "$(openssl rand -hex 32)" | gcloud secrets versions add eventphoto-retention-secret --data-file=- --project PROJECT_ID
```

`retention-secret` is required the same way the six above are — Cloud Run
refuses to start the revision if any referenced secret has no version at
all, whether or not any event ever turns retention on. `join-code` is the
one whose *value* barely matters: give it any version, even an empty one
(see **Before the event**), since `JOIN_CODE` only seeds Daglig's join code
on first start and an empty value just means Daglig gets a generated one.

`printf '%s'` rather than `echo`, and never an interactive `--data-file=-`
prompt: both of those store a trailing newline in the secret. AppConfig trims
whitespace on load so it is no longer fatal, but a webhook path that silently
differs by one character is not a good debugging session.

Those are bash (Git Bash on Windows is fine). **PowerShell has no `printf`**,
and `'value' | gcloud ... --data-file=-` there appends a CRLF. Use a temp
file instead, one secret at a time:

```powershell
$f = (New-TemporaryFile).FullName
[System.IO.File]::WriteAllText($f, 'YOUR_VALUE', [System.Text.UTF8Encoding]::new($false))
gcloud secrets versions add eventphoto-bot-token --data-file="$f" --project PROJECT_ID
Remove-Item $f
```

The three random values (`webhook-secret`, `webhook-path`, `cookie-key`) can
be generated straight into the command so they are never typed, pasted or
shown:

```bash
printf '%s' "$(openssl rand -hex 32)" | gcloud secrets versions add eventphoto-webhook-path --data-file=- --project PROJECT_ID
```

Then run `deploy` again. That run applies cleanly and registers the webhook.
Every subsequent deploy is a single run.

This whole sequence — steps 0 to 5 and both `deploy` runs — was walked
end to end against a fresh personal GCP project on 2026-09-21, and the
steps above are what actually worked, including the two corrections this
paragraph sits between (step 0, and the PowerShell form of the secret
commands). The failure points are the documented ones: the first `deploy`
run stops at "Terraform apply" with `Secret .../versions/latest was not
found` listing all seven secrets. Cloud Run's startup probe hits `/healthz`
from inside the service; do not be alarmed if that same path answers 404
through an outbound proxy while the revision reports healthy.

Note that `workflow_dispatch` workflows only appear in the Actions tab once
the workflow file is on the repository's **default branch** — a first deploy
from a feature branch has nothing to click until that branch is merged.

## Upgrading an existing deployment to this release

This section is for a project that was already running the app before events
existed, not for a brand new one — a new project just follows **One-time
setup** and **Before the event** above, which already cover everything here.

**Before the first deploy of this release:**

- **Add a version to the `<name>-retention-secret` secret**, the same as any
  other required secret in step 5 above — Cloud Run mounts it and the
  revision fails to start without one, whether or not retention is ever
  turned on for any event:
  `openssl rand -hex 32 | gcloud secrets versions add eventphoto-retention-secret --data-file=- --project PROJECT_ID`.
  `JOIN_CODE` needs no new action; it already has a version from the original
  deploy, and this release still reads it, just once, differently (below).
- **If deploying through GitHub Actions, re-apply `infra/wif` once.** This
  release's retention sweep is scheduled by both deploy paths, and the
  Actions path needs a role the deploy service account did not need before:
  `roles/cloudscheduler.admin`, now listed in `infra/wif/main.tf`. Re-running
  `terraform apply` there (see **One-time setup**, step 2) grants it. Skip
  this if you only ever deploy with `deploy.ps1` — the workstation path acts
  as your own `gcloud` identity, not the WIF service account.
- Optionally set `SCHEDULER_REGION` (a GitHub repository variable) or pass
  `-SchedulerRegion` to `deploy.ps1` if `europe-west1` is not a good place
  for the retention job to run from — see **Events and retention** above.

**What the first start after upgrading does, automatically, no action
needed:** it rewrites `state.json` into the events shape — a `Daglig` event
is created from the old settings, every image gets `EventId = "daglig"`,
every sender becomes a Daglig member with their old status carried over, and
`JOIN_CODE` seeds Daglig's join code if it was set. The pre-upgrade file is
kept at `state/state-prev.json`, for as long as nothing else writes to
`state.json` afterwards — which in practice means as long as nobody
approves, deletes or otherwise changes anything.

**To roll back** before anything has been written since the upgrade: redeploy
the previous image, then copy `state-prev.json` over `state.json` *before*
that previous version starts and writes anything of its own. Once something
has written to `state.json` post-upgrade, `state-prev.json` is stale and a
rollback means accepting whatever has changed since, not a clean revert.

## Deploying

Both the application and the Terraform apply run from **GitHub Actions**.
That is the path this project deploys through; `infra/deploy.ps1` (below)
is the workstation fallback, kept interchangeable with it rather than as the
normal route.

Three workflows live in `.github/workflows/` — `plan`, `deploy` and
`destroy`. **`deploy` runs automatically on every push to `master`**, and can
also be started by hand; `plan` and `destroy` are manual
(`workflow_dispatch`-only) and never run on a push. They authenticate to GCP
with Workload Identity Federation — no service account key is stored in
GitHub. This path needs all four steps of **One-time setup** above, including
the GitHub repository variables.

Because a merge to `master` is a deploy, anyone who can merge can reach GCP
with the deploy service account's permissions, and can trigger the webhook
re-registration described below. Branch protection on `master` is what gates
that — `infra/wif/`'s trust condition only checks which *repository* the run
came from, not which branch.

### Running the workflows

All three live under the **Actions** tab, run via **Run workflow**; `deploy`
additionally fires on its own whenever something lands on `master`.

- **plan** — takes an optional `image_digest` (leave it as the default
  `placeholder` before the first image has ever been built — the same
  bootstrap convention `deploy.ps1` uses). Writes the plan to the run's job
  summary, so reviewing it doesn't mean digging through logs.
- **deploy** — builds and pushes the image, applies pinned to the resulting
  digest, and registers the webhook. Runs on every push to `master` as well
  as on demand. **Do not deploy mid-event, and do not merge to `master`
  mid-event** — they are now the same act. Registering the webhook drops
  whatever Telegram is holding at that moment, so a photo sent in that window
  vanishes with no error to either the sender or the admin. If something must
  change mid-event, use `gcloud run services update` by hand, which never
  touches the webhook registration, and leave `master` alone until the event
  is over.
- **destroy** — takes `confirm_project_id`. It must match this repository's
  `GCP_PROJECT_ID` variable exactly, or the job fails before touching GCP.
  Runs `terraform destroy` in `infra/` — see **Teardown** below for what
  this does *not* remove.

A concurrency group shared by all three (`eventphoto-terraform`) means only
one of plan/deploy/destroy runs at a time, so two runs can't write to the
same remote state simultaneously.

### Deploying from a workstation (`infra/deploy.ps1`) — fallback

The same sequence the `deploy` workflow runs, from a machine with `gcloud`,
`docker`, `terraform` and PowerShell 7. Use it when the Actions path is
unavailable (no network access to GitHub, a broken WIF trust, or debugging
the apply interactively). Needs the state bucket from **One-time setup**
above to already exist — `-StateBucket` is where its name goes.

```bash
pwsh infra/deploy.ps1 -ProjectId my-event-project -StateBucket eventphoto-tfstate-my-event-project
```

Add `-HostingSite tbg-event-photos` to also put the friendly `.web.app` URL in
front of the service — the workstation equivalent of the `HOSTING_SITE`
repository variable. Omit it and nothing Firebase-related is created. Whichever
path is used, use the same value: they share one state, so deploying from one
with the site set and the other without it makes each apply undo the other's.

The script bootstraps the registry, bucket and secret resources, prints the
`gcloud secrets versions add` commands for you to run by hand (secret values
never pass through Terraform — a value passed as a Terraform variable ends up
in plaintext in state), builds and pushes the image, applies the full
configuration pinned to that image's digest, and registers the Telegram
webhook. It is safe to rerun: pass `-SkipBuild` to skip rebuilding the image
on a rerun where only the secrets or the Terraform apply needed a retry.

The two paths stay interchangeable: `deploy.ps1` and the `deploy` workflow
run the same steps — a targeted bootstrap apply for the Artifact Registry
repository and the secret resources (needed on a fresh project before
there's anywhere to push an image or add a secret version to), build, push,
capture the digest, `terraform apply` pinned to it, register the webhook the
same way — and both read and write the same Terraform state.

`-StateBucket` points `terraform init` at exactly the state the workflows
use, on purpose — see **One-time setup** above. Omitting it fails
immediately with a message pointing back there, rather than a raw Terraform
backend-initialization error.

On success it prints the slideshow URL, the admin URL, and confirms the
webhook registered. If any step fails, the script stops there and reports
which command failed. Fix whatever it reports (usually a missing secret
version or a `gcloud` auth issue) and rerun the whole command — every step
is safe to repeat.

### The friendly URL (Firebase Hosting)

Cloud Run's own URL —
`https://eventphoto-<project number>.europe-north1.run.app` — is not something
a guest can read off a screen or type on a phone. Setting `HOSTING_SITE` (or
`-HostingSite`) puts a Firebase Hosting site in front of the service, so the
same app also answers on `https://<site>.web.app` with a certificate already
in place. It costs nothing, needs no domain, and `infra/main.tf` creates all
of it — there is no separate `firebase deploy` step and no `firebase.json`.

Leave it unset and nothing changes: no Firebase APIs are enabled, no Firebase
resources are created, and the `run.app` URL stays the only way in.

Three things to know before setting it.

**Claim the name early.** The site id is globally unique across all of
Firebase and immutable once created. `tbg-event-photos` being free today does
not mean it is free on the evening of the event. Set the variable and run a
deploy (or a targeted apply) as soon as the name is decided.

**Adding Firebase to the project cannot be undone.** Every other resource here
disappears on `terraform destroy`; this one does not. Once Firebase is added
to a GCP project it stays added, and destroy only drops it from state. That is
harmless for a project deleted wholesale after the event — which is the
intended lifecycle — but it does mean the "destroy returns the project to
clean" property no longer strictly holds. If a project must stay pristine,
leave `HOSTING_SITE` unset.

**The webhook still goes to the `run.app` URL.** Telegram intake is
deliberately not routed through Hosting: a certificate or rewrite problem on
the friendly URL then cannot cost a photo. Both deploy paths print the Hosting
URL for humans and the direct URL underneath it, and register the webhook
against the direct one.

The rewrite sends every path to the Cloud Run service, which must be in one of
the regions Firebase Hosting supports for Cloud Run rewrites. `europe-north1`
(this project's default) is one of them; if you move the service to an unusual
region, check [the supported
list](https://firebase.google.com/docs/hosting/cloud-run) first.

The deploy service account needs `roles/firebase.editor` to create any of
this. `infra/wif/` grants it — if WIF was applied before this section existed,
reapply it (`terraform apply` in `infra/wif/`) before the first deploy with
`HOSTING_SITE` set, or the apply fails with a permission error on
`google_firebase_project`.

**Every deploy publishes a new Hosting version, on purpose.** The version's
config carries the deployed image digest as an `X-Event-Photo-Build` response
header, so each apply creates a version and releases it — Firebase's own model,
and what `curl -I https://<site>.web.app/` reads back to tell you which build
is actually being served.

It is also what keeps the pipeline recoverable. If the version were identical
between deploys — which it was until the header was added, because the rewrite
names the Cloud Run *service* and not the image — then Terraform creates one
version ever, and losing `google_firebase_hosting_release.app` from state is a
dead end: the apply plans a release, and Firebase refuses it with

```
Error creating Release: googleapi: Error 400: Can't release to
`sites/<site>/channels/live`: supplied version `sites/<site>/versions/<id>`
is the current active version.
```

on every subsequent run, because the only version that exists is the one
already live. With a fresh version per deploy there is always something new to
release, so a state that has lost the release heals on the next run instead of
needing an operator to find the live release id and `terraform import` it.

### Teardown — what `terraform destroy` (or the `destroy` workflow) does not remove

`terraform destroy` in `infra/` removes the Cloud Run service, the images
bucket, the seven secrets, the runtime service account and the Artifact
Registry repository — the same set `deploy.ps1`'s counterpart apply created.
It does **not** touch:

- **The `<name>-retention` Cloud Scheduler job** — created with `gcloud`, not
  Terraform, the same way the webhook registration is. See the "Delete the
  retention Cloud Scheduler job" step under **After the event** above.
- **The Terraform state bucket** (`infra/backend/`) — destroying it would
  delete the record of what to destroy, so it's deliberately outside this
  module's own blast radius.
- **The Workload Identity Federation pool, provider and deploy service
  account** (`infra/wif/`) — these authenticate GitHub Actions to GCP and
  are meant to outlive any one event, not be recreated per event.
- **Firebase itself, if `HOSTING_SITE` was set.** The Hosting site, its
  versions and its releases are destroyed normally; the project's *membership*
  of Firebase is not, because Google provides no way to remove it. Destroy
  drops `google_firebase_project` from state and leaves the project marked as
  a Firebase project. Nothing runs and nothing bills as a result — see **The
  friendly URL** above.

If the project itself is also being retired (not just this one event), both
have to be torn down explicitly and separately, from the repository root
(`terraform -chdir=`, not `cd`, so the second command isn't run from inside
the first one's directory looking for a path that doesn't exist there):

```bash
# The state bucket has versioning ON, deliberately (see infra/backend/main.tf)
# — a corrupted or bad-apply state write should be recoverable — and no
# force_destroy. That means `terraform destroy` on its own cannot remove it:
# GCS refuses to delete a non-empty bucket, and this one always holds at
# least eventphoto/state/default.tfstate (infra/main.tf's backend prefix)
# plus every noncurrent version of it, not just whatever the "current" file
# is. Empty it first. --recursive against the
# object wildcard implies --all-versions (gcloud's own doc for this exact
# case), so this clears the version history too, and it doesn't touch the
# bucket resource itself — that stays for `terraform destroy` to remove next.
bucket_name=$(terraform -chdir=infra/backend output -raw bucket_name)
gcloud storage rm --recursive --all-versions "gs://$bucket_name/**" --project PROJECT_ID

terraform -chdir=infra/wif     destroy -var "project_id=PROJECT_ID" -var "repository=OWNER/REPO"
terraform -chdir=infra/backend destroy -var "project_id=PROJECT_ID"
```

Confirm the state bucket is actually gone afterwards
(`gcloud storage buckets list --project PROJECT_ID`) — the spec's teardown
criterion is "confirm the bucket is gone, not just emptied", and that applies
just as much to this bucket as to the images one.

If the project is staying in use for a future event, leave both alone —
that's the point of bootstrapping them once.

## Build notes

`SixLabors.ImageSharp` is pinned to **3.1.12** deliberately — this is not
accidental staleness. 4.x gates the build on a Six Labors licence-key
enrollment check that is a hard error, not a warning, in Release
configuration, and the container build (`Dockerfile`) always builds Release.
Bumping this package without first sorting out a Six Labors licence key will
break the image build with an error that has no obvious connection to
whatever change prompted the bump.
