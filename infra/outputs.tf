output "service_url" {
  value       = google_cloud_run_v2_service.app.uri
  description = "Where the slideshow and admin pages live. Also the webhook base."
}

output "hosting_url" {
  value       = local.hosting_enabled ? google_firebase_hosting_site.app[0].default_url : ""
  description = "The friendly https://<site>.web.app URL in front of the service. Empty when hosting_site is unset. The webhook still goes to service_url."
}

output "bucket_name" {
  value       = google_storage_bucket.images.name
  description = "Download originals/ from here before destroying."
}

output "service_account_email" {
  value = google_service_account.runtime.email
}
