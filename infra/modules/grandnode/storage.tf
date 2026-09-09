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

  # CORS is required for the frontend bundles, not optional styling: a script tag
  # carrying integrity/crossorigin="anonymous" is fetched as a CORS request, and a
  # response without Access-Control-Allow-Origin makes the browser discard it. The
  # page then renders with no JavaScript at all and no error anyone will notice.
  # Only created when a bundle CDN origin is configured.
  dynamic "blob_properties" {
    for_each = length(var.bundle_cors_origins) > 0 ? [1] : []
    content {
      cors_rule {
        allowed_origins = var.bundle_cors_origins
        allowed_methods = ["GET", "HEAD"]
        allowed_headers = ["*"]
        exposed_headers = ["*"]
        #the bundles are immutable per version prefix, so a long preflight cache costs nothing
        max_age_in_seconds = 3600
      }
    }
  }

  tags = local.tags
}

# Storefront JavaScript and CSS, when they are served from storage rather than from
# inside the image. Public-read: browsers fetch these directly.
#
# Uploads go under a version prefix (bundles/v1/, bundles/v2/) so a release is atomic -
# the new files sit alongside the old ones and nothing switches until
# FrontendAssetSettings.BaseUrl is updated. Rolling back is pointing it at the previous
# prefix; no rebuild, no redeploy.
#
# Integrity hashes deliberately do NOT live here. They are published through the
# application into FrontendAssetSettings, so that whoever holds this storage key can
# replace a bundle but cannot make a browser execute it - the hash will not match.
resource "azurerm_storage_container" "bundles" {
  count = var.enable_bundle_storage ? 1 : 0

  name                  = "bundles"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "blob"
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

# Data-plane access for whoever publishes frontend bundle releases.
#
# Scoped to the storage account rather than the bundles container: az's upload-batch
# enumerates the container before writing, and a container-scoped grant is not enough
# for that on a hierarchical-namespace account.
#
# The alternative is --auth-mode key, which hands out one secret covering every
# container on the account with no way to tell afterwards who used it.
resource "azurerm_role_assignment" "bundle_publishers" {
  for_each = toset(var.bundle_publisher_object_ids)

  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = each.value

  # Do NOT set skip_service_principal_aad_check here. It does not merely skip a lookup -
  # it makes the provider declare principalType "ServicePrincipal", and Azure rejects the
  # assignment outright for a user object ID:
  #   UnmatchedPrincipalType: The PrincipalId '...' has type 'User', which is different
  #   from specified PrinciaplType 'ServicePrincipal'.
  # Leaving it unset lets Azure infer the type, which works for users, groups and service
  # principals alike. A service principal created moments earlier may need a retry while
  # it replicates.
}

locals {
  # Trailing slash is REQUIRED: AzurePictureService concatenates this with the
  # container name to build public image URLs.
  blob_endpoint = azurerm_storage_account.this.primary_blob_endpoint
}
