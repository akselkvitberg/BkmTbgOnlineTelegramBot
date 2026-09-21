# Event photo bot

Guests scan a QR on the screen to join a Telegram bot and send it photos, an
admin approves them, and approved photos run as a slideshow on a projector.
Named photographers can be set to skip the queue, and a sender can be banned
outright, which also pulls everything they already sent. The whole web side
sits behind one shared password. It is built to be deployed for one event and
destroyed afterwards.

- **What to do on the day, and how to tear it down:** [docs/RUNBOOK.md](docs/RUNBOOK.md)
- **Why it is built this way:** [telegram-online-bot-spec.md](telegram-online-bot-spec.md)

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

The container build always builds Release; see **Build notes** in the runbook
for the one pinned package that matters.
