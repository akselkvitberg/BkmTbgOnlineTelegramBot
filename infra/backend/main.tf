# Terraform state backend — bootstrap only, not part of the app's lifecycle.
#
# Chicken-and-egg: infra/main.tf's own state lives in a GCS bucket (so GitHub
# Actions has something durable to read between runs), but that bucket has to
# exist and be reachable before `terraform init` in infra/ can point at it.
# This tiny config creates just that bucket, applied once by hand with its
# own local state — never migrated into itself. Ordering for a brand new
# setup:
#
#   1. cd infra/backend && terraform init && terraform apply   (this config — local state)
#   2. cd infra/wif     && terraform init && terraform apply   (independent of this one)
#   3. cd infra         && terraform init -backend-config="bucket=<bucket_name from step 1>"
#
# See docs/RUNBOOK.md for the full one-time setup, including the gcloud
# commands that feed GitHub's repository variables.
#
# This bucket is intentionally outside infra/'s own Terraform: it holds that
# module's state, so the module destroying itself must not be able to take
# the bucket recording what to destroy down with it. `terraform destroy` in
# infra/ never touches this config, and removing this bucket is a separate,
# deliberate step — see docs/RUNBOOK.md's teardown section.

terraform {
  required_version = ">= 1.9"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}

resource "google_project_service" "storage" {
  service = "storage.googleapis.com"

  # Disabling APIs on destroy is a common source of hung or failed teardowns
  # (matches the reasoning in infra/main.tf).
  disable_on_destroy = false
}

resource "google_storage_bucket" "tfstate" {
  name     = "${var.name}-tfstate-${var.project_id}"
  location = var.region

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  # On, unlike the app bucket: a corrupted or bad-apply state write should be
  # recoverable by rolling back to a prior object version. The app bucket
  # keeps versioning off deliberately (see infra/main.tf) because it needs
  # deleted image objects to actually disappear; that reasoning doesn't apply
  # to a state file, which is small and never holds personal data.
  versioning { enabled = true }

  depends_on = [google_project_service.storage]
}
