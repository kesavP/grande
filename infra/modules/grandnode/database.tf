###############################################################################
# Cosmos DB for MongoDB vCore
#
# vCore, not the RU-based Mongo API. The RU offering is a compatibility layer
# over a different storage engine; GrandNode relies on genuine MongoDB
# behaviour - GridFS, array update operators, aggregation - that it implements
# incompletely.
###############################################################################

resource "azurerm_mongo_cluster" "this" {
  name                = "mongo-${local.suffix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location

  administrator_username = var.mongo_admin_username
  administrator_password = local.mongo_password

  shard_count            = var.mongo_shard_count
  compute_tier           = var.mongo_compute_tier
  storage_size_in_gb     = var.mongo_storage_gb
  high_availability_mode = var.mongo_high_availability ? "ZoneRedundantPreferred" : "Disabled"
  version                = var.mongo_server_version

  tags = local.tags

  lifecycle {
    # Changing the admin password in place is not supported by every provider
    # version; rotate it out of band rather than letting a plan recreate the
    # cluster and destroy the database.
    ignore_changes = [administrator_password]
  }
}

# Permits access from Azure services, including the Container Apps environment.
# Replace with a private endpoint before going live.
resource "azurerm_mongo_cluster_firewall_rule" "allow_azure" {
  count = var.mongo_allow_azure_services ? 1 : 0

  name             = "allow-azure-services"
  mongo_cluster_id = azurerm_mongo_cluster.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

locals {
  # vCore connection URI. retrywrites=false is required - the vCore endpoint
  # does not support retryable writes.
  mongo_connection_string = join("", [
    "mongodb+srv://",
    var.mongo_admin_username,
    ":",
    urlencode(local.mongo_password),
    "@",
    azurerm_mongo_cluster.this.name,
    ".global.mongocluster.cosmos.azure.com/",
    var.mongo_database_name,
    "?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000"
  ])
}
