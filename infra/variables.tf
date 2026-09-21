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

variable "hosting_site" {
  type        = string
  default     = ""
  description = "Firebase Hosting site id, e.g. tbg-event-photos for https://tbg-event-photos.web.app in front of the Cloud Run service. Globally unique across all of Firebase and immutable once created. Empty disables Hosting, leaving the run.app URL as the only way in."
}

variable "image_digest" {
  type        = string
  description = "Full image reference pinned by digest, e.g. europe-north1-docker.pkg.dev/PROJECT/eventphoto/app@sha256:abc123. Pinned by digest rather than tag so terraform apply is honest about what changes."
}
