###############################################################################
# Persistent volumes (Azure Files) for the container's stateful paths
#
# Everything inside a Container Apps replica is ephemeral, and this application
# restarts more often than you would expect: installing a plugin calls
# IHostApplicationLifetime.StopApplication() (PluginController.cs:159), the admin
# panel has a Restart button, scale-to-zero stops the replica, and every deploy
# rolls it.
#
# Most state is already externalised by this module - the connection string via
# ConnectionStrings__Mongodb, the data-protection key ring and product images via
# blob storage. Two paths are not, and these mounts cover them:
#
#   /app/App_Data                  InstalledPlugins.cfg (which plugins are
#                                  installed), Download, TempUploads
#   /app/wwwroot/assets/images     elFinder file-manager uploads - IMediaFileStore
#                                  is hardcoded to a FileSystemStore over
#                                  WebRootPath regardless of blob configuration
#
# IMPORTANT: an Azure Files mount REPLACES the directory the image ships. Both
# paths have seeded content in the image - App_Data carries appsettings.json,
# which the host reads at startup and cannot boot without, plus Resources/ and
# UrlRewrite.xml. Seed the shares BEFORE enabling the mounts. See infra/README.md.
###############################################################################

locals {
  # volume name -> mount path inside the container
  container_volumes = {
    appdata = "/app/App_Data"
    media   = "/app/wwwroot/assets/images"
  }
}

resource "azurerm_storage_share" "this" {
  for_each = var.enable_persistent_volumes ? local.container_volumes : {}

  name               = each.key
  storage_account_id = azurerm_storage_account.this.id
  quota              = var.volume_quota_gb
}

resource "azurerm_container_app_environment_storage" "this" {
  for_each = var.enable_persistent_volumes ? local.container_volumes : {}

  name                         = each.key
  container_app_environment_id = azurerm_container_app_environment.this.id
  account_name                 = azurerm_storage_account.this.name
  share_name                   = azurerm_storage_share.this[each.key].name
  access_key                   = azurerm_storage_account.this.primary_access_key
  access_mode                  = "ReadWrite"
}
