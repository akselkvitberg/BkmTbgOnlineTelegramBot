# Event photo bot

Guests scan a QR on the screen to join a Telegram bot and send it photos (or
post them in a Telegram group the bot has been added to), an admin approves them, and approved photos run as a slideshow on a projector.
Named photographers can be set to skip the queue, and a sender can be banned
outright, which also pulls everything they already sent. The whole web side
sits behind one shared password. It is built to be deployed for one event and
destroyed afterwards.

One event runs by default: Daglig, always open and never deleted, for a
church's day-to-day photos. A special event (a wedding, a concert) can run
alongside it, with its own join QR, its own screen at `/show?event=<id>`, and
photos kept apart from Daglig's. Telegram groups are routed to whichever
event they should feed, in `/admin/telegram`. Old photos can be cleared out
automatically after a set number of days, per event, always keeping the
newest approved ones.

- **What to do on the day, and how to tear it down:** [docs/RUNBOOK.md](docs/RUNBOOK.md)
- **Why it is built this way:** [telegram-online-bot-spec.md](telegram-online-bot-spec.md)
- **What it would take to keep it:** [docs/BEYOND-ONE-EVENT.md](docs/BEYOND-ONE-EVENT.md)

## Layout

| Path | What |
| --- | --- |
| `src/EventPhotoBot/` | The ASP.NET Core app — Telegram webhook, admin pages, slideshow |
| `tests/EventPhotoBot.Tests/` | Unit tests |
| `infra/` | Terraform for the event's own resources (Cloud Run, bucket, secrets, registry) |
| `infra/backend/` | Bootstrap: the GCS bucket holding `infra/`'s Terraform state |
| `infra/wif/` | Bootstrap: Workload Identity Federation trust for GitHub Actions |
| `infra/deploy.ps1` | Workstation deploy, the fallback for the Actions path |
| `.github/workflows/` | `plan`, `deploy`, `destroy` — all manual, none run on a push |

## Deploying

From the **Actions** tab: run the `deploy` workflow. It authenticates to GCP
with Workload Identity Federation (no service account key in GitHub), builds
and pushes the image, applies Terraform pinned to that image's digest, and
registers the Telegram webhook.

Before the first deploy in a new GCP project there is a one-time setup — the
state bucket, the federation trust, the repository variables, and the secret
values added by hand with `gcloud`. All of it is in
[docs/RUNBOOK.md](docs/RUNBOOK.md#one-time-setup-before-the-first-deploy-either-path).

## Local build and test

```bash
dotnet test
```

## Running it locally

```bash
dotnet run --project src/EventPhotoBot --launch-profile local
```

Open http://localhost:5055 and log in with the password `dev`. The `local`
profile sets `LOCAL_DEV=true`, which:

- stores state and photos under `src/EventPhotoBot/.local-data/` instead of the
  bucket (delete the folder to start over);
- replaces Telegram with an offline stand-in, so nothing is sent anywhere and the
  join QR points at a bot that does not exist;
- adds a guest simulator at http://localhost:5055/dev, which joins as a guest and
  sends photos through the same code path the webhook uses, and shows the bot's
  replies. Pick which event to join with, and tap any button the bot sends back
  (to switch events, say) the same way a real Telegram client would. Tick "I en
  gruppe" to add the bot to a simulated Telegram group and post there as a
  member instead.

`LOCAL_DEV` refuses to start outside the Development environment.

The container build always builds Release; see **Build notes** in the runbook
for the one pinned package that matters.
