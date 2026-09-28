#requires -Version 7
<#
  Build, push, apply, register the webhook. Safe to rerun.
  Secret VALUES never pass through Terraform: they are set here with gcloud,
  because a secret passed as a Terraform variable ends up in plaintext in state.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProjectId,
    [string] $StateBucket,
    [string] $Region = 'europe-north1',
    [string] $Name = 'eventphoto',
    # Firebase Hosting site id -> https://<site>.web.app in front of the service.
    # Omit to skip Hosting entirely and use the run.app URL. See docs/RUNBOOK.md.
    [string] $HostingSite,
    # Cloud Scheduler is not offered in every region; check with `gcloud scheduler locations list`.
    [string] $SchedulerRegion = 'europe-west1',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$registry = "$Region-docker.pkg.dev/$ProjectId/$Name"

# Native (non-cmdlet) commands don't throw on failure even with
# $ErrorActionPreference = 'Stop' — they just set $LASTEXITCODE. A half-applied
# Terraform run or a push that silently failed is worse than one that stops
# here, so every native call below is checked explicitly.
function Assert-Success([string] $What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# infra/main.tf declares a `backend "gcs"` block with no bucket (a backend
# block can't reference a variable), so `terraform init` here needs the same
# `-backend-config` the GitHub Actions workflows pass. This is deliberate,
# not incidental: a workstation apply and a CI apply must land in the same
# state, or they can each believe they own resources the other created,
# and `terraform destroy` from either path stops being trustworthy — see
# docs/RUNBOOK.md. A plain, unhelpful Terraform backend-initialization error
# is the wrong way for a first-time operator to discover this, so it's
# checked here instead, in the same style as every other guard in this
# script.
if ([string]::IsNullOrWhiteSpace($StateBucket)) {
    throw "-StateBucket is required. Apply infra/backend/ once first (terraform init && " +
        "terraform apply -var project_id=$ProjectId), then pass its bucket_name output here: " +
        "terraform -chdir=infra/backend output -raw bucket_name. The workstation deploy and the " +
        "GitHub Actions workflows share one remote state on purpose - see docs/RUNBOOK.md."
}

Write-Host '==> Bootstrapping registry, bucket and secrets (first apply may partially fail; rerun)' -ForegroundColor Cyan
terraform -chdir="$PSScriptRoot" init -backend-config="bucket=$StateBucket"
Assert-Success 'terraform init'
terraform -chdir="$PSScriptRoot" apply `
    -target=google_artifact_registry_repository.images `
    -target=google_secret_manager_secret.secrets `
    -var "project_id=$ProjectId" -var "region=$Region" -var "name=$Name" `
    -var "hosting_site=$HostingSite" `
    -var 'image_digest=placeholder'
Assert-Success 'terraform apply (bootstrap)'

Write-Host '==> Secret values' -ForegroundColor Cyan
Write-Host 'Add any that are missing, then rerun. AppConfig trims whitespace on load, so a'
Write-Host 'trailing newline from typing a value and pressing Enter is no longer fatal - but'
Write-Host 'the form below avoids adding one in the first place:'
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-bot-token      --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-webhook-secret --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-webhook-path   --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-admin-password --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-cookie-key     --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-join-code     --data-file=- --project $ProjectId"
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-retention-secret --data-file=- --project $ProjectId"
Write-Host ''
Write-Host 'Generate the random ones (webhook-secret, webhook-path, cookie-key, retention-secret) with:  openssl rand -hex 32'
Write-Host 'join-code is yours to choose and goes in the QR on the screen: letters, digits,'
Write-Host '_ and - only, at most 64 characters. Anything else fails startup, because Telegram'
Write-Host 'silently drops a deep-link payload outside that set.'
Write-Host ''

if (-not $SkipBuild) {
    Write-Host '==> Build and push' -ForegroundColor Cyan
    gcloud auth configure-docker "$Region-docker.pkg.dev" --quiet --project $ProjectId
    Assert-Success 'gcloud auth configure-docker'
    docker build -t "$registry/app:latest" $repo
    Assert-Success 'docker build'
    docker push "$registry/app:latest"
    Assert-Success 'docker push'
}

# Pin by digest so terraform apply is honest about what changes.
$digest = (docker inspect --format='{{index .RepoDigests 0}}' "$registry/app:latest")
Assert-Success 'docker inspect'
if ([string]::IsNullOrWhiteSpace($digest)) {
    throw "Could not resolve a digest for $registry/app:latest — build and push it first (omit -SkipBuild)."
}
Write-Host "==> Image: $digest" -ForegroundColor Cyan

Write-Host '==> Apply' -ForegroundColor Cyan
terraform -chdir="$PSScriptRoot" apply `
    -var "project_id=$ProjectId" -var "region=$Region" -var "name=$Name" `
    -var "hosting_site=$HostingSite" `
    -var "image_digest=$digest"
Assert-Success 'terraform apply'

$serviceUrl = terraform -chdir="$PSScriptRoot" output -raw service_url
Assert-Success 'terraform output service_url'
if ([string]::IsNullOrWhiteSpace($serviceUrl)) { throw 'service_url output is empty.' }

# Empty unless -HostingSite was given, and deliberately not fatal when it is:
# Hosting is the nice-to-read front door, not the intake path. The webhook
# below is registered against $serviceUrl either way, so a Hosting problem
# never costs a photo.
$hostingUrl = terraform -chdir="$PSScriptRoot" output -raw hosting_url
Assert-Success 'terraform output hosting_url'
$publicUrl = if ([string]::IsNullOrWhiteSpace($hostingUrl)) { $serviceUrl } else { $hostingUrl }

Write-Host '==> Registering the Telegram webhook' -ForegroundColor Cyan
# PowerShell's native-command capture splits multi-line output into a string[] (one
# element per line), not a single string. A secret value should never have embedded
# newlines, but if one somehow does, "bot$botToken" below would interpolate the array
# space-joined rather than throwing — a silent corruption of the token, not a loud
# failure. Out-String forces a single string in every case, and Trim() drops the
# trailing newline gcloud's own output adds.
$botToken      = (gcloud secrets versions access latest --secret "$Name-bot-token" --project $ProjectId | Out-String).Trim()
Assert-Success 'gcloud secrets versions access (bot-token)'
$webhookPath   = (gcloud secrets versions access latest --secret "$Name-webhook-path" --project $ProjectId | Out-String).Trim()
Assert-Success 'gcloud secrets versions access (webhook-path)'
$webhookSecret = (gcloud secrets versions access latest --secret "$Name-webhook-secret" --project $ProjectId | Out-String).Trim()
Assert-Success 'gcloud secrets versions access (webhook-secret)'

if ([string]::IsNullOrWhiteSpace($botToken) -or [string]::IsNullOrWhiteSpace($webhookPath) `
        -or [string]::IsNullOrWhiteSpace($webhookSecret)) {
    throw 'One or more secrets have no version yet. Add the missing ones (see above) and rerun.'
}

$response = Invoke-RestMethod -Method Post -Uri "https://api.telegram.org/bot$botToken/setWebhook" -Body @{
    url                  = "$serviceUrl/tg/$webhookPath"
    secret_token         = $webhookSecret
    max_connections      = 4
    # Safe here because this call only fires when the webhook URL, path or secret is
    # being (re)registered for the first time, before anyone has the bot's handle - there
    # is nothing pending to drop yet. Do NOT run this deploy script (or otherwise call
    # setWebhook) mid-event: it would discard any update Telegram is holding for a
    # temporarily-unreachable webhook, and a photo sent in that window vanishes with no
    # error to either the sender or the admin. A mid-event redeploy should go through
    # `gcloud run services update` instead, which never touches the webhook registration.
    drop_pending_updates = 'true'
}

if (-not $response.ok) { throw "setWebhook failed: $($response.description)" }

Write-Host '==> Scheduling the daily retention sweep' -ForegroundColor Cyan
$retentionSecret = (gcloud secrets versions access latest --secret "$Name-retention-secret" --project $ProjectId | Out-String).Trim()
Assert-Success 'gcloud secrets versions access (retention-secret)'
if ([string]::IsNullOrWhiteSpace($retentionSecret)) { throw 'retention-secret has no version yet. Add it (see above) and rerun.' }

# Created with gcloud, not Terraform, for the same reason as every secret value here:
# the header would otherwise sit in plaintext in Terraform state.
$job = "$Name-retention"
# attempt-deadline: Cloud Scheduler's own default for an HTTP target is 3 minutes,
# well under the sweep's 900s Cloud Run request timeout - a job dropped mid-sweep
# cancels the rest of RetentionSweep's delete loop and orphans object bytes whose
# state record is already gone. --format=none: `jobs create/update` otherwise print
# the resulting Job resource, headers and all, putting X-Retention-Secret in the
# console output; `describe` above is already redirected to null for the same reason.
$jobArgs = @('--location', $SchedulerRegion, '--project', $ProjectId,
    '--schedule', '15 3 * * *', '--time-zone', 'Europe/Oslo',
    '--uri', "$serviceUrl/internal/retention", '--http-method', 'POST',
    '--attempt-deadline', '900s', '--format=none')
gcloud scheduler jobs describe $job --location $SchedulerRegion --project $ProjectId *> $null
if ($LASTEXITCODE -eq 0) {
    gcloud scheduler jobs update http $job @jobArgs --update-headers "X-Retention-Secret=$retentionSecret"
} else {
    gcloud scheduler jobs create http $job @jobArgs --headers "X-Retention-Secret=$retentionSecret"
}
Assert-Success 'gcloud scheduler jobs create/update'

Write-Host ''
Write-Host "Slideshow: $publicUrl/show"    -ForegroundColor Green
Write-Host "Admin:     $publicUrl/admin/queue" -ForegroundColor Green
if ($publicUrl -ne $serviceUrl) {
    Write-Host "Direct:    $serviceUrl (bypasses Hosting; also the webhook base)" -ForegroundColor DarkGray
}
Write-Host 'Webhook registered.' -ForegroundColor Green
