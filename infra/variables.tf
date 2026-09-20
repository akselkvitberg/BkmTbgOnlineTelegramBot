variable "project_id" {
  type        = string
  description = "GCP project. This project is created for the event and destroyed after it."
}

variable "region" {
  type        = string
  default     = "europe-north1"
  description = "Latency, and keeping the data in the EU."
}

variable "name" {
  type        = string
  default     = "eventphoto"
  description = "Prefix for every resource name."
}

variable "event_name" {
  type        = string
  description = "Shown on the slideshow's empty state."
}

variable "image_digest" {
  type        = string
  description = "Full image reference pinned by digest, e.g. europe-north1-docker.pkg.dev/PROJECT/eventphoto/app@sha256:abc123. Pinned by digest rather than tag so terraform apply is honest about what changes."
}
