###############################################################################
# Storage: product media and the data-protection key ring
#
# Both containers must exist BEFORE the application starts. The data-protection
# container in particular: without it, the key ring falls back to the container
# filesystem and every restart signs out every user.
###############################################################################

resource "azurerm_storage_account" "this" {
  name                     = "st${local.compact_suffix}"
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"

  # Hierarchical namespace - directory operations are markedly faster than on
  # flat blob storage, and it is what a downstream lakehouse would want anyway.
  is_hns_enabled = true

  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = true

  tags = local.tags
}

# Product images are served directly to browsers, so blob-level public read.
resource "azurerm_storage_container" "media" {
  name                  = "media"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "blob"
}

# Data-protection keys. Private - never public.
resource "azurerm_storage_container" "dpkeys" {
  name                  = "dpkeys"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

locals {
  # Trailing slash is REQUIRED: AzurePictureService concatenates this with the
  # container name to build public image URLs.
  blob_endpoint = azurerm_storage_account.this.primary_blob_endpoint
}
