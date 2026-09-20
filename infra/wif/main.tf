# Workload Identity Federation — bootstrap only, not part of the app's
# lifecycle. Applied once by hand with its own local state, same reasoning as
# infra/backend/ (see that config's header comment).
#
# This creates the keyless trust between this one GitHub repository and GCP,
# and the service account the deploy workflow acts as. It is independent of
# infra/backend/ — apply either first — but both must exist before the
# GitHub Actions workflows in .github/workflows/ can run.
#
# Not touched by `terraform destroy` in infra/, and not recreated per event:
# the pool, provider and deploy service account are meant to outlive any one
# event's Cloud Run service. See docs/RUNBOOK.md's teardown section for
# removing this deliberately, and the GHA report for why each IAM role below
# is granted.

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
}

resource "google_project_service" "iam" {
  for_each = toset([
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "sts.googleapis.com",
  ])

  service = each.value

  # Disabling APIs on destroy is a common source of hung or failed teardowns
  # (matches the reasoning in infra/main.tf).
  disable_on_destroy = false
}

# ---------------------------------------------------------------------------
# Pool and provider
# ---------------------------------------------------------------------------

resource "google_iam_workload_identity_pool" "github" {
  workload_identity_pool_id = "${var.name}-github-pool"
  display_name              = "GitHub Actions"
  description               = "Keyless federation for ${var.repository}"

  depends_on = [google_project_service.iam]
}

resource "google_iam_workload_identity_pool_provider" "github" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "${var.name}-github-provider"
  display_name                       = "GitHub"

  attribute_mapping = {
    "google.subject"       = "assertion.sub"
    "attribute.repository" = "assertion.repository"
    "attribute.ref"        = "assertion.ref"
  }

  # Locked to this one repository. Without this condition, any workflow in
  # any GitHub repository that learned this provider's resource name could
  # exchange its own OIDC token for one impersonating the deploy service
  # account — this line is the difference between "keyless" and "an open
  # door with extra steps". Deliberately not also restricted to a ref/branch:
  # every workflow that uses this provider is workflow_dispatch-only, so
  # there is no "untrusted branch" push path to additionally guard against —
  # only who is allowed to manually invoke it, which is a repository
  # permission, not something WIF can see.
  attribute_condition = "assertion.repository == \"${var.repository}\""

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }

  depends_on = [google_project_service.iam]
}

# ---------------------------------------------------------------------------
# The identity the workflows act as
# ---------------------------------------------------------------------------

resource "google_service_account" "deploy" {
  account_id   = "${var.name}-deploy"
  display_name = "Event photo bot - GitHub Actions deploy"
}

# Lets a workflow running in the named repository impersonate the deploy
# service account. This is a second, independent narrowing on top of the
# provider's attribute_condition above: that condition controls who can mint
# a token from the pool at all, this binding controls which service account
# a token bearing that repository's identity may then become.
resource "google_service_account_iam_member" "wif_binding" {
  service_account_id = google_service_account.deploy.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository/${var.repository}"
}

# ---------------------------------------------------------------------------
# What the deploy service account may do in the target project.
#
# Every role here exists because infra/main.tf's `terraform apply` creates or
# reconfigures a resource of that type from scratch, or because the deploy
# workflow's build/push step needs it. None of them grant access to secret
# *values* — secretmanager.admin manages secret resources and their IAM
# policy, never lets the holder read a version's payload. See the GHA report
# for the one-line justification of each entry, restated here for anyone
# reading only this file.
# ---------------------------------------------------------------------------

locals {
  deploy_roles = [
    "roles/serviceusage.serviceUsageAdmin", # enable/track the APIs infra/main.tf's google_project_service turns on
    "roles/storage.admin",                  # create/configure the images bucket and its IAM, and read/write Terraform state objects in the backend bucket
    "roles/artifactregistry.admin",         # create the Artifact Registry repository, and push the built image to it
    "roles/secretmanager.admin",            # create the 5 secret *resources* and their IAM bindings only — never touches a version's value
    "roles/iam.serviceAccountAdmin",        # create the Cloud Run runtime service account (google_service_account.runtime in infra/main.tf)
    "roles/iam.serviceAccountUser",         # let Terraform attach that runtime service account to the Cloud Run service (actAs)
    "roles/run.admin",                      # create/update the Cloud Run service and set its IAM policy (the allUsers invoker binding)
  ]
}

resource "google_project_iam_member" "deploy" {
  for_each = toset(local.deploy_roles)

  project = var.project_id
  role    = each.value
  member  = "serviceAccount:${google_service_account.deploy.email}"
}
