#requires -Version 7
<#
  Build, push, apply, register the webhook. Safe to rerun.
  Secret VALUES never pass through Terraform: they are set here with gcloud,
  because a secret passed as a Terraform variable ends up in plaintext in state.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProjectId,
    [Parameter(Mandatory)][string] $EventName,
    [string] $Region = 'europe-north1',
    [string] $Name = 'eventphoto',
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

Write-Host '==> Bootstrapping registry, bucket and secrets (first apply may partially fail; rerun)' -ForegroundColor Cyan
terraform -chdir="$PSScriptRoot" init
Assert-Success 'terraform init'
terraform -chdir="$PSScriptRoot" apply `
    -target=google_artifact_registry_repository.images `
    -target=google_secret_manager_secret.secrets `
    -var "project_id=$ProjectId" -var "event_name=$EventName" -var "region=$Region" -var "name=$Name" `
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
Write-Host ''
Write-Host 'Generate the three random ones (webhook-secret, webhook-path, cookie-key) with:  openssl rand -hex 32'
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
    -var "project_id=$ProjectId" -var "event_name=$EventName" -var "region=$Region" -var "name=$Name" `
    -var "image_digest=$digest"
Assert-Success 'terraform apply'

$serviceUrl = terraform -chdir="$PSScriptRoot" output -raw service_url
Assert-Success 'terraform output service_url'
if ([string]::IsNullOrWhiteSpace($serviceUrl)) { throw 'service_url output is empty.' }

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

Write-Host ''
Write-Host "Slideshow: $serviceUrl/show"    -ForegroundColor Green
Write-Host "Admin:     $serviceUrl/admin/queue" -ForegroundColor Green
Write-Host 'Webhook registered.' -ForegroundColor Green
