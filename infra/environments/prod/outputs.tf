output "application_url" { value = module.grandnode.application_url }
output "install_url" { value = module.grandnode.install_url }
output "container_registry_name" { value = module.grandnode.container_registry_name }
output "resource_group_name" { value = module.grandnode.resource_group_name }
output "storage_account_name" { value = module.grandnode.storage_account_name }
output "next_steps" { value = module.grandnode.next_steps }

output "mongo_connection_string" {
  value     = module.grandnode.mongo_connection_string
  sensitive = true
}
