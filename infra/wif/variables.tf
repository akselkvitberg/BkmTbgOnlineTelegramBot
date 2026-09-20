variable "project_id" {
  type        = string
  description = "GCP project the deploy service account is granted roles on (the same project infra/ deploys the app into)."
}

variable "repository" {
  type        = string
  default     = "akselkvitberg/BkmTbgOnlineTelegramBot"
  description = "GitHub 'owner/repo' allowed to mint tokens against this provider. Locking the provider's attribute_condition to this exact value is what makes the federation keyless-safe — double check it before applying if this config is ever reused for a fork or a rename. The comparison is case-sensitive and GitHub's OIDC token carries the account's own canonical casing (not necessarily what a clone URL happens to show), so a casing mismatch here fails closed with an opaque STS/auth error at the workflow's authentication step rather than a clear one at apply time. Confirm the exact value before applying, e.g. `gh api repos/OWNER/REPO --jq .full_name` — see docs/RUNBOOK.md's one-time setup section."
}

variable "name" {
  type        = string
  default     = "eventphoto"
  description = "Prefix for the pool, provider and service account names — kept aligned with infra/'s var.name."
}
