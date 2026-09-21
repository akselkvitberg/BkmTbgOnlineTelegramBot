output "service_url" {
  value       = google_cloud_run_v2_service.app.uri
  description = "Where the slideshow and admin pages live. Also the webhook base."
}

output "bucket_name" {
  value       = google_storage_bucket.images.name
  description = "Download originals/ from here before destroying."
}

output "service_account_email" {
  value = google_service_account.runtime.email
}
