output "bucket_name" {
  value       = google_storage_bucket.tfstate.name
  description = "Pass as `terraform init -backend-config=\"bucket=<this>\"` in infra/, and store it as the TF_STATE_BUCKET repository variable for the GitHub Actions workflows."
}
