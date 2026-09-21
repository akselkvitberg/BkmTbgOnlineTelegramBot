variable "project_id" {
  type        = string
  description = "GCP project that will hold the Terraform state bucket (the same project infra/ deploys the app into)."
}

variable "region" {
  type        = string
  default     = "europe-north1"
  description = "Kept aligned with infra/'s region, though the bucket has no latency-sensitive traffic of its own."
}

variable "name" {
  type        = string
  default     = "eventphoto"
  description = "Prefix for the bucket name — kept aligned with infra/'s var.name."
}
