# Event photo bot — runbook

This is what to follow on the day, and the checks that prove the system is
actually working before you rely on it. The whole system is designed to be
destroyed after the event — see **After** — so treat teardown as a step on
this list, not a someday task.

## Before the event

- [ ] Bot created via BotFather, token stored (`gcloud secrets versions add
      eventphoto-bot-token --data-file=- --project PROJECT_ID`),
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
      requests return `304 Not Modified`, and Cloud Run's logs
      (`gcloud run services logs read eventphoto --project PROJECT_ID
      --region europe-north1`) show no calls into Cloud Storage during that
      same window — the manifest is served from the in-memory generation
      counter, not from a bucket read, on every poll that finds nothing new.
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
      is running and polling, deploy a new no-op revision (e.g. rerun
      `infra/deploy.ps1 -SkipBuild`, or `gcloud run services update
      eventphoto --project PROJECT_ID --region europe-north1` with no real
      change) to force the old instance to be replaced. Pass: the slideshow
      keeps going from its next poll with no images or approvals lost —
      state is reloaded from `state.json` at the new instance's startup.
- [ ] **`terraform destroy` leaves nothing behind.** Run
      `terraform -chdir=infra destroy` (in a scratch project first if you
      want to check this without touching the real event's data). Pass: the
      bucket, the four secrets, and the Cloud Run service are all gone
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
