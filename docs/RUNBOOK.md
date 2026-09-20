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
- [ ] Whitelist collected via pairing mode, then pairing mode turned off (see
      "Collecting the whitelist" below)
- [ ] Programme and menu images uploaded via the images page and pinned as
      recurring
- [ ] Takeover set and cleared once, so whoever runs the screen has done it
      before they need to under pressure
- [ ] Full path tested from a real phone: send a photo, it appears in the
      queue, approve it, it appears on the slideshow
- [ ] Portrait photo from both an iPhone and an Android checked for
      orientation
- [ ] Slideshow run for an hour on the actual display machine, to catch sleep
      and memory issues before the event does
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

### Collecting the whitelist (pairing mode)

1. Open admin settings, turn pairing mode on.
2. Share the bot's handle with the group.
3. Each person who messages the bot gets nothing stored yet — they appear
   under "Seen while pairing" in settings with their numeric Telegram id.
   Nothing they send is stored while they are unlisted.
4. Add the ones you want, one tap each, from that list.
5. **Turn pairing mode off.** Anyone not added is now declined, silently
   rate-limited to one reply per minute so a stranger poking the bot can't
   turn it into a spam relay.

Adding a sender takes effect on their very next message — no redeploy needed.

## During the event

The admin watches the approval queue on a phone. If nobody is free to
moderate, turn on auto-approve for trusted senders in settings and mark
everyone trusted — the whitelist is then the only control left, which is a
reasonable posture for a known group.

**If the screen freezes:** reload the page. The slideshow polls the manifest
every couple of seconds and picks up from there; nothing is lost by
reloading.

**If one image is stuck on screen (a forgotten takeover):** every admin page
shows a banner naming the image holding takeover, with a one-tap clear. A
takeover set to "until I clear it" is the most likely way to strand a photo
on the wall for the rest of the night — that banner is the fix.

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
- [ ] `terraform -chdir=infra destroy` — leaves no bucket, secret or service
      behind
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

If the event repeats (a second party, a second Sunday), destroying and
redeploying from scratch is the intended pattern — there is no state that
carries over on purpose, and rerunning `infra/deploy.ps1` against a fresh
project takes minutes.

## Acceptance criteria

These are checks a person runs against the deployed service. Each one is
written so it has an unambiguous pass or fail — do them in order, since
several depend on state left by the one before.

- [ ] **Pairing mode reveals a sender's id.** Open admin settings, turn on
      pairing mode, message the bot from a phone that has never talked to it.
      Pass: the phone's numeric id appears under "Seen while pairing" within
      a few seconds.
- [ ] **Adding a sender takes effect immediately.** Add that phone with one
      tap, turn pairing mode off, and send a photo from the same phone
      *without redeploying anything*. Pass: the photo appears in the
      approval queue.
- [ ] **A whitelisted photo reaches the queue within five seconds.** Send one
      photo from the whitelisted phone and time it with a stopwatch from
      send to appearance in `/admin/queue`. Pass: under five seconds. If you
      send an album of five and it's slower, that's expected — Telegram
      delivers album members as separate updates in series, not the bot
      being slow.
- [ ] **A non-whitelisted sender is declined and nothing is stored.** From a
      second, never-added phone, send a photo. Pass: the phone gets a decline
      message, and `gcloud storage ls -r gs://BUCKET_NAME/originals` (before
      and after, compared) shows no new object.
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
      `/admin/images`, `/admin/settings`, and an image URL under `/img/...`
      directly. Pass: each one returns `401` or redirects to the login page
      — never the actual content.
- [ ] **Portrait orientation is correct from both platforms.** Send one
      portrait photo from an iPhone and one from an Android phone. Pass:
      both display upright on the slideshow, not rotated or letterboxed
      sideways.
- [ ] **Killing the instance mid-event loses nothing.** While the slideshow
      is running and polling, force the old instance to be replaced with
      `gcloud run services update eventphoto --project PROJECT_ID --region
      europe-north1` (with no real change). Pass: the slideshow keeps going
      from its next poll with no images or approvals lost — state is
      reloaded from `state.json` at the new instance's startup. Do **not**
      use `infra/deploy.ps1 -SkipBuild` for this check while the event is
      live: it re-registers the webhook with `drop_pending_updates: true`,
      which discards anything Telegram is holding for the webhook at that
      moment — a photo sent in that window vanishes with no error to the
      sender or the admin. `deploy.ps1` is for before/after the event, or a
      scratch project; `gcloud run services update` is the mid-event tool.
- [ ] **`terraform destroy` leaves nothing behind.** Run
      `terraform -chdir=infra destroy` (in a scratch project first if you
      want to check this without touching the real event's data). Pass: the
      bucket, all five secrets, and the Cloud Run service are all gone
      afterwards — check with `gcloud storage buckets list`, `gcloud secrets
      list`, and `gcloud run services list`, all scoped `--project
      PROJECT_ID`.

## Deploying

```bash
pwsh infra/deploy.ps1 -ProjectId my-event-project -EventName "Summer Party"
```

The script bootstraps the registry, bucket and secret resources, prints the
`gcloud secrets versions add` commands for you to run by hand (secret values
never pass through Terraform — a value passed as a Terraform variable ends up
in plaintext in state), builds and pushes the image, applies the full
configuration pinned to that image's digest, and registers the Telegram
webhook. It is safe to rerun: pass `-SkipBuild` to skip rebuilding the image
on a rerun where only the secrets or the Terraform apply needed a retry.

On success it prints the slideshow URL, the admin URL, and confirms the
webhook registered.

If any step fails, the script stops there and reports which command failed.
Fix whatever it reports (usually a missing secret version or a `gcloud`
auth issue) and rerun the whole command — every step is safe to repeat.

## Deploying from GitHub Actions

An alternative to running `deploy.ps1` from a workstation: three manual
(`workflow_dispatch`-only) workflows in `.github/workflows/` — `plan`,
`deploy` and `destroy`. Nothing here runs on a push; every one of them has to
be started by hand from the Actions tab. They authenticate to GCP with
Workload Identity Federation — no service account key is stored in GitHub.

Both paths stay interchangeable: `deploy.ps1` and the `deploy` workflow run
the same sequence (build, push, capture the digest, `terraform apply` pinned
to it, register the webhook the same way), and both read and write the same
Terraform state once the one-time setup below is done.

### One-time setup

Do this once per GCP project, not per event. Both bootstrap configs use
their own **local** state (never migrated anywhere) and are applied by hand
from a workstation with `gcloud` already authenticated against the project —
neither is ever run from CI.

**1. State bucket (`infra/backend/`)** — creates the GCS bucket that
`infra/main.tf` uses as its remote backend once this is done.

```bash
cd infra/backend
terraform init
terraform apply -var "project_id=PROJECT_ID"
terraform output -raw bucket_name    # -> save as the TF_STATE_BUCKET value below
```

**2. Workload Identity Federation (`infra/wif/`)** — creates the pool,
provider (locked to this one GitHub repository) and the service account the
workflows act as, and grants that service account the roles it needs to run
Terraform against `infra/` and to push images to Artifact Registry. See the
GHA report (`.superpowers/sdd/2026-09-20-event-photo-bot/gha-report.md`) for
why each role is there.

```bash
cd infra/wif
terraform init
terraform apply -var "project_id=PROJECT_ID" -var "repository=OWNER/REPO"
terraform output -raw workload_identity_provider    # -> GCP_WORKLOAD_IDENTITY_PROVIDER
terraform output -raw deploy_service_account_email  # -> GCP_DEPLOY_SERVICE_ACCOUNT
```

**3. First `terraform init` against the new backend** — after step 1, the
next `terraform init` in `infra/` (whether from `deploy.ps1` or a workflow)
needs to point at the new bucket explicitly:

```bash
cd infra
terraform init -backend-config="bucket=<bucket_name from step 1>"
```

Terraform will offer to migrate the existing local state into the bucket —
accept that once, from a workstation, so the state used up to now is not
orphaned.

**4. GitHub repository variables** — under **Settings → Secrets and
variables → Actions → Variables**, set:

| Variable | Value |
| --- | --- |
| `GCP_PROJECT_ID` | The project id (same one used above) |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | Output of step 2 |
| `GCP_DEPLOY_SERVICE_ACCOUNT` | Output of step 2 |
| `TF_STATE_BUCKET` | Output of step 1 |
| `GCP_REGION` | Optional — defaults to `europe-north1` if unset |
| `APP_NAME` | Optional — defaults to `eventphoto` if unset |

None of these are secret — they're project ids, resource names and a bucket
name. No bot token, admin password or signing key is ever configured as a
GitHub secret or variable; those stay in Secret Manager and are added the
same way regardless of which deploy path is used:

```bash
printf '%s' 'YOUR_VALUE' | gcloud secrets versions add eventphoto-bot-token --data-file=- --project PROJECT_ID
```

### Running the workflows

All three live under the **Actions** tab, run via **Run workflow**.

- **plan** — takes `event_name` and an optional `image_digest` (leave it as
  the default `placeholder` before the first image has ever been built —
  the same bootstrap convention `deploy.ps1` uses). Writes the plan to the
  run's job summary, so reviewing it doesn't mean digging through logs.
- **deploy** — takes `event_name`. Builds and pushes the image, applies
  pinned to the resulting digest, and registers the webhook. **Do not run
  this mid-event** — same warning as `deploy.ps1 -SkipBuild`: registering the
  webhook drops whatever Telegram is holding for the moment the webhook is
  unreachable. Mid-event, use `gcloud run services update` by hand instead.
- **destroy** — takes `confirm_project_id`. It must match this repository's
  `GCP_PROJECT_ID` variable exactly, or the job fails before touching GCP.
  Runs `terraform destroy` in `infra/` — see **Teardown** below for what
  this does *not* remove.

A concurrency group shared by all three (`eventphoto-terraform`) means only
one of plan/deploy/destroy runs at a time, so two runs can't write to the
same remote state simultaneously.

### Teardown — what `terraform destroy` (or the `destroy` workflow) does not remove

`terraform destroy` in `infra/` removes the Cloud Run service, the images
bucket, the five secrets, the runtime service account and the Artifact
Registry repository — the same set `deploy.ps1`'s counterpart apply created.
It does **not** touch:

- **The Terraform state bucket** (`infra/backend/`) — destroying it would
  delete the record of what to destroy, so it's deliberately outside this
  module's own blast radius.
- **The Workload Identity Federation pool, provider and deploy service
  account** (`infra/wif/`) — these authenticate GitHub Actions to GCP and
  are meant to outlive any one event, not be recreated per event.

If the project itself is also being retired (not just this one event), both
have to be torn down explicitly and separately:

```bash
cd infra/wif     && terraform destroy -var "project_id=PROJECT_ID" -var "repository=OWNER/REPO"
cd infra/backend && terraform destroy -var "project_id=PROJECT_ID"
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
