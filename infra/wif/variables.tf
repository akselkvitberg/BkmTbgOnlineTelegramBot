variable "project_id" {
  type        = string
  description = "GCP project the deploy service account is granted roles on (the same project infra/ deploys the app into)."
}

variable "repository" {
  type        = string
  default     = "akselkvitberg/BkmTbgOnlineTelegramBot"
  description = "GitHub 'owner/repo' allowed to mint tokens against this provider. Locking the provider's attribute_condition to this exact value is what makes the federation keyless-safe — double check it before applying if this config is ever reused for a fork or a rename."
}

variable "name" {
  type        = string
  default     = "eventphoto"
  description = "Prefix for the pool, provider and service account names — kept aligned with infra/'s var.name."
}
