terraform {
  required_version = ">= 1.9"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
    # Every Firebase resource is beta-only, so the optional Hosting front door
    # below needs a second provider alongside the first. Both point at the same
    # project; nothing else in this config uses google-beta.
    google-beta = {
      source  = "hashicorp/google-beta"
      version = "~> 6.0"
    }
  }

  # Remote state for CI (GitHub Actions has no workstation to keep local state
  # on between runs) — see infra/backend/ for the bucket this points at and
  # the chicken-and-egg ordering that creates it first.
  #
  # A backend block is evaluated before any variable, so the bucket name
  # can't be var.project_id or anything else computed — Terraform requires it
  # to be a literal. The standard workaround is a *partial* backend
  # configuration: leave the bucket out here and supply it at init time:
  #   terraform init -backend-config="bucket=<infra/backend output bucket_name>"
  # The CI workflows (.github/workflows/) do this. A workstation running
  # infra/deploy.ps1 does not pass -backend-config, so `terraform init` there
  # now needs the same flag before it will succeed against this backend —
  # see docs/RUNBOOK.md and the GHA report's concerns section.
  backend "gcs" {
    prefix = "eventphoto/state"
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}

provider "google-beta" {
  project = var.project_id
  region  = var.region

  # Firebase's own docs call for this: without it the Firebase API calls bill
  # their quota to whichever project the *credentials* belong to rather than
  # the one being configured, which on a service-account apply is not
  # necessarily this project at all.
  user_project_override = true
}

# ---------------------------------------------------------------------------
# APIs
# ---------------------------------------------------------------------------

locals {
  apis = [
    "run.googleapis.com",
    "artifactregistry.googleapis.com",
    "secretmanager.googleapis.com",
    "storage.googleapis.com",
    "iamcredentials.googleapis.com",
  ]

  # Only when a Hosting site is actually asked for. Adding Firebase to a
  # project is one of the few things here that cannot be undone (see
  # google_firebase_project below), so a project that doesn't want the
  # friendly URL shouldn't get the APIs that lead there either.
  hosting_enabled = var.hosting_site != ""

  hosting_apis = [
    "firebase.googleapis.com",
    "firebasehosting.googleapis.com",
  ]
}

resource "google_project_service" "apis" {
  for_each = toset(concat(local.apis, local.hosting_enabled ? local.hosting_apis : []))

  service = each.value

  # Disabling APIs on destroy is a common source of hung or failed teardowns.
  disable_on_destroy = false
}

# ---------------------------------------------------------------------------
# Storage — images and state.json
# ---------------------------------------------------------------------------

resource "google_storage_bucket" "images" {
  name     = "${var.name}-${var.project_id}"
  location = var.region

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  versioning { enabled = false }

  # force_destroy so teardown removes the bucket rather than failing on contents.
  force_destroy = true

  lifecycle_rule {
    condition {
      age            = 30
      matches_prefix = ["originals/", "display/", "thumbs/"]
    }
    action { type = "Delete" }
  }

  depends_on = [google_project_service.apis]
}

# state/ is deliberately outside the lifecycle rule above. Each write creates a
# new object with a fresh creation time so it would survive an active event
# either way, but an unscoped age rule on the object holding all the metadata is
# not something to leave to chance.

# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

resource "google_artifact_registry_repository" "images" {
  location      = var.region
  repository_id = var.name
  format        = "DOCKER"

  depends_on = [google_project_service.apis]
}

# ---------------------------------------------------------------------------
# Secrets — resources only. Values are added outside Terraform:
#   gcloud secrets versions add eventphoto-bot-token --data-file=-
# A secret passed as a Terraform variable ends up in plaintext in state.
# ---------------------------------------------------------------------------

locals {
  secret_ids = {
    bot_token      = "${var.name}-bot-token"
    webhook_secret = "${var.name}-webhook-secret"
    webhook_path   = "${var.name}-webhook-path"
    admin_password = "${var.name}-admin-password"
    cookie_key     = "${var.name}-cookie-key"
    join_code      = "${var.name}-join-code"
  }
}

resource "google_secret_manager_secret" "secrets" {
  for_each  = local.secret_ids
  secret_id = each.value

  replication {
    user_managed {
      replicas { location = var.region }
    }
  }

  depends_on = [google_project_service.apis]
}

# ---------------------------------------------------------------------------
# Runtime identity
# ---------------------------------------------------------------------------

resource "google_service_account" "runtime" {
  account_id   = "${var.name}-run"
  display_name = "Event photo bot runtime"
}

resource "google_storage_bucket_iam_member" "objects" {
  bucket = google_storage_bucket.images.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.runtime.email}"
}

resource "google_secret_manager_secret_iam_member" "access" {
  for_each  = google_secret_manager_secret.secrets
  secret_id = each.value.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.runtime.email}"
}

# ---------------------------------------------------------------------------
# The service
# ---------------------------------------------------------------------------

resource "google_cloud_run_v2_service" "app" {
  name     = var.name
  location = var.region

  deletion_protection = false
  ingress             = "INGRESS_TRAFFIC_ALL"

  template {
    service_account = google_service_account.runtime.email

    # Nothing holds a connection open, so nothing pins an instance: Cloud Run
    # bills request time only and the service costs nothing between events.
    scaling {
      min_instance_count = 0
      max_instance_count = 1
    }

    # A 20 MB getFile plus derivatives, with margin.
    timeout = "120s"

    containers {
      image = var.image_digest

      resources {
        limits = {
          cpu    = "1"
          memory = "1Gi"
        }
        cpu_idle          = true # billed for request time only
        startup_cpu_boost = true # keeps the cold start to a few seconds
      }

      env {
        name  = "BUCKET_NAME"
        value = google_storage_bucket.images.name
      }

      env {
        name  = "EVENT_NAME"
        value = var.event_name
      }

      dynamic "env" {
        for_each = {
          TELEGRAM_BOT_TOKEN      = local.secret_ids.bot_token
          TELEGRAM_WEBHOOK_SECRET = local.secret_ids.webhook_secret
          TELEGRAM_WEBHOOK_PATH   = local.secret_ids.webhook_path
          ADMIN_PASSWORD          = local.secret_ids.admin_password
          COOKIE_SIGNING_KEY      = local.secret_ids.cookie_key
          JOIN_CODE               = local.secret_ids.join_code
        }

        content {
          name = env.key
          value_source {
            secret_key_ref {
              secret  = env.value
              version = "latest"
            }
          }
        }
      }

      startup_probe {
        http_get { path = "/healthz" }
        initial_delay_seconds = 3
        period_seconds        = 3
        failure_threshold     = 10
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_iam_member.access,
    google_storage_bucket_iam_member.objects,
  ]
}

# Telegram and guests need to reach the service; the app's own password is the gate.
resource "google_cloud_run_v2_service_iam_member" "public" {
  name     = google_cloud_run_v2_service.app.name
  location = google_cloud_run_v2_service.app.location
  role     = "roles/run.invoker"
  member   = "allUsers"
}

# ---------------------------------------------------------------------------
# The friendly URL — optional, set var.hosting_site to enable.
#
# Firebase Hosting hands out https://<site>.web.app with a certificate already
# in place, for free and without owning a domain, and rewrites every path to
# the Cloud Run service above. That is the whole reason it is here: a URL a
# guest can read off a screen instead of the run.app one.
#
# The run.app URL keeps working and stays the webhook base — see deploy.ps1.
# Nothing about intake depends on Hosting, so DNS or certificate trouble on
# this side cannot cost a photo.
#
# Two things here are unlike everything else in this file:
#   - google_firebase_project cannot be undone. Once Firebase is added to a
#     GCP project it stays added; `terraform destroy` only drops it from
#     state. Harmless for a project that is deleted wholesale after the
#     event, but it does mean this one resource breaks the "destroy returns
#     the project to clean" property the rest of the config has.
#   - site_id is globally unique across all of Firebase and immutable. Claim
#     it early; a name taken on the evening of the event is a name gone.
# ---------------------------------------------------------------------------

resource "google_firebase_project" "hosting" {
  count    = local.hosting_enabled ? 1 : 0
  provider = google-beta
  project  = var.project_id

  depends_on = [google_project_service.apis]
}

resource "google_firebase_hosting_site" "app" {
  count    = local.hosting_enabled ? 1 : 0
  provider = google-beta
  project  = var.project_id
  site_id  = var.hosting_site

  depends_on = [google_firebase_project.hosting]
}

# A version is the config itself; a release is what publishes it. Changing the
# rewrite below replaces both, which is how Hosting works rather than churn to
# design out.
resource "google_firebase_hosting_version" "app" {
  count    = local.hosting_enabled ? 1 : 0
  provider = google-beta
  site_id  = google_firebase_hosting_site.app[0].site_id

  config {
    rewrites {
      glob = "**"

      run {
        service_id = google_cloud_run_v2_service.app.name
        region     = google_cloud_run_v2_service.app.location
      }
    }
  }
}

resource "google_firebase_hosting_release" "app" {
  count        = local.hosting_enabled ? 1 : 0
  provider     = google-beta
  site_id      = google_firebase_hosting_site.app[0].site_id
  version_name = google_firebase_hosting_version.app[0].name
  message      = "${var.name} — ${var.event_name}"
}

# ---------------------------------------------------------------------------
# Cost guard. The event costs well under a euro; this exists so a forgotten
# teardown cannot quietly become a monthly bill.
# ---------------------------------------------------------------------------

# Requires a billing account id and the billingbudgets API. Left commented
# because it is the one resource here that touches billing-account-level IAM,
# which the event project's owner may not hold. Enable it if you do.
#
# resource "google_billing_budget" "guard" {
#   billing_account = var.billing_account
#   display_name    = "${var.name} guard"
#   budget_filter { projects = ["projects/${var.project_id}"] }
#   amount { specified_amount { currency_code = "EUR" units = "20" } }
#   threshold_rules { threshold_percent = 0.5 }
#   threshold_rules { threshold_percent = 1.0 }
# }
