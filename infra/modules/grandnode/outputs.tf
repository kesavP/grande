output "application_url" {
  description = "Public URL of the storefront. The admin panel is at this URL + /admin."
  value       = "https://${azurerm_container_app.this.ingress[0].fqdn}"
}

output "install_url" {
  description = "Run this once to seed the database, then set enable_installer = false and re-apply."
  value       = "https://${azurerm_container_app.this.ingress[0].fqdn}/install"
}

output "container_registry" {
  description = "Registry login server. Build with: az acr build -r <name> -t grandnode:<tag> -f Dockerfile ."
  value       = azurerm_container_registry.this.login_server
}

output "container_registry_name" {
  description = "Registry name for the az acr build -r argument."
  value       = azurerm_container_registry.this.name
}

output "mongo_cluster_name" {
  description = "Cosmos DB for MongoDB vCore cluster name."
  value       = azurerm_mongo_cluster.this.name
}

output "mongo_connection_string" {
  description = "The connection string the application is configured with (built from known vCore URI format)."
  value       = local.mongo_connection_string
  sensitive   = true
}

output "mongo_connection_strings_from_azure" {
  description = <<-EOT
    What Azure itself reports for the cluster, with credential placeholders left
    in. Compare against mongo_connection_string on the first apply - if the host
    suffix differs, prefer this one and open an issue against infra/database.tf.
  EOT
  value       = azurerm_mongo_cluster.this.connection_strings
  sensitive   = true
}

output "storage_account_name" {
  description = "Storage account holding the media and dpkeys containers."
  value       = azurerm_storage_account.this.name
}

output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "next_steps" {
  description = "What to do after the first apply, in order."
  value       = <<-EOT
    1. Build and push the image - the app cannot start until the tag exists:
         az acr build -r ${azurerm_container_registry.this.name} -t ${var.image_repository}:${var.image_tag} -f Dockerfile .
       from the repository root, then:
         terraform apply -replace=azurerm_container_app.this

    2. Install with a WARM replica. The /install POST runs the whole seeding
       routine in one request; with min_replicas = 0 it cold-starts first and
       the browser times out before the app ever sees it:
         terraform apply -var min_replicas=1 -var cpu=1.0 -var memory=2Gi

    3. Open https://${azurerm_container_app.this.ingress[0].fqdn}/install
       - Mongo fields are ignored; the configured connection string wins.
       - Set Collation to "-None-" on Cosmos vCore. It does NOT default to it -
         the dropdown pre-selects your UI language, and vCore rejects collation
         with "Command create failed: Collation is currently not supported."

    4. RESTART once installation finishes. Not optional: InstallController calls
       ResetCache(), which latches DatabaseIsInstalled() to false for the life of
       the process, and the app then reports itself unhealthy.
         az containerapp revision restart -g ${azurerm_resource_group.this.name} \
           -n ${azurerm_container_app.this.name} \
           --revision $(az containerapp revision list -g ${azurerm_resource_group.this.name} \
             -n ${azurerm_container_app.this.name} --query "[?properties.active].name | [0]" -o tsv)

    5. Close the installer and return to free-tier scale:
         enable_installer = false, min_replicas = 0, cpu = 0.5, memory = "1Gi"

    6. Optional - persist plugin state and file-manager uploads: create the
       shares, seed them, set volumes_seeded = true, then
       enable_persistent_volumes = true. See infra/README.md.
  EOT
}
