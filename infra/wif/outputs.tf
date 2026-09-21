output "workload_identity_provider" {
  value       = google_iam_workload_identity_pool_provider.github.name
  description = "Full resource name — store as the GCP_WORKLOAD_IDENTITY_PROVIDER repository variable for the GitHub Actions workflows."
}

output "deploy_service_account_email" {
  value       = google_service_account.deploy.email
  description = "Store as the GCP_DEPLOY_SERVICE_ACCOUNT repository variable for the GitHub Actions workflows."
}
