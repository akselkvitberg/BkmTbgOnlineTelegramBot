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
  # door with extra steps". Deliberately not also restricted to a ref/branch,
  # though the reason is narrower than it once was: the deploy workflow now
  # runs on push to master as well as on demand, so a merge to master reaches
  # GCP with this identity, and the gate on that is who can push to or merge
  # into master — a repository permission, not something WIF can see. A
  # ref condition pinning master would match the deploy trigger but would
  # break `plan` and `destroy`, which stay manual-only and are legitimately
  # dispatched from a feature branch. Branch protection on master is the
  # control that matters here, not this line.
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
# workflow needs it directly. One of them is broader than "manage resources
# only": roles/secretmanager.admin includes secretmanager.versions.access,
# i.e. it CAN read a secret version's payload, not just create/configure the
# secret resource and its IAM policy. That is not an oversight to narrow —
# .github/workflows/deploy.yml's webhook-registration step genuinely reads
# three payloads (the bot token, webhook path and webhook secret) to call
# Telegram's setWebhook, the same values infra/deploy.ps1 reads from a
# human's own gcloud session for the same call. There is no predefined role
# that creates secret resources without also being able to read their
# versions — Google ships secretVersionManager as the read/write-versions
# role precisely because admin already includes it, not as a narrower
# alternative to it — so this grant is the correct one, not an approximation
# of a narrower one. What keeps the exposure bounded is that the three
# values this role lets the deploy SA read are masked the instant the
# workflow captures them — see that step's own comment. See the GHA report
# for the one-line justification of every other entry, restated here for
# anyone reading only this file.
# ---------------------------------------------------------------------------

locals {
  deploy_roles = [
    "roles/serviceusage.serviceUsageAdmin", # enable/track the APIs infra/main.tf's google_project_service turns on
    "roles/storage.admin",                  # create/configure the images bucket and its IAM, and read/write Terraform state objects in the backend bucket
    "roles/artifactregistry.admin",         # create the Artifact Registry repository, and push the built image to it
    "roles/secretmanager.admin",            # create the 5 secret resources and their IAM bindings, AND read a version's payload — deploy.yml's webhook step needs the latter; see the comment block above
    "roles/iam.serviceAccountAdmin",        # create the Cloud Run runtime service account (google_service_account.runtime in infra/main.tf)
    "roles/iam.serviceAccountUser",         # let Terraform attach that runtime service account to the Cloud Run service (actAs)
    "roles/run.admin",                      # create/update the Cloud Run service and set its IAM policy (the allUsers invoker binding)

    # Only matters when infra/main.tf's var.hosting_site is set — the optional
    # Firebase Hosting front door that gives the event a <site>.web.app URL
    # instead of the run.app one. Granted unconditionally because this config
    # is applied once per project and deliberately does not track per-event
    # choices; on a project with no Firebase resources it grants access to
    # nothing.
    #
    # One role rather than two, and deliberately not the obvious
    # roles/firebase.admin. Adding Firebase to the project needs
    # firebase.projects.update, which roles/firebasehosting.admin does not
    # carry; creating the site and publishing its versions and releases needs
    # firebasehosting.sites.create/update, which Hosting's coarse IAM folds
    # into the site permissions rather than exposing per version or release.
    # roles/firebase.editor is the narrowest predefined role holding both —
    # it differs from roles/firebase.admin only by not also granting
    # firebase.projects.delete, which nothing here needs.
    "roles/firebase.editor",
  ]
}

resource "google_project_iam_member" "deploy" {
  for_each = toset(local.deploy_roles)

  project = var.project_id
  role    = each.value
  member  = "serviceAccount:${google_service_account.deploy.email}"
}
