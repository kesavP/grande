###############################################################################
# Resource group, naming and generated secrets
###############################################################################

locals {
  suffix = "${var.name_prefix}-${var.environment}"

  # Registry and storage account names permit only lowercase alphanumerics.
  compact_suffix = "${var.name_prefix}${var.environment}"

  tags = merge({
    application = "grandnode"
    environment = var.environment
    managed_by  = "terraform"
  }, var.tags)
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.suffix}"
  location = var.location
  tags     = local.tags
}

###############################################################################
# Generated secrets
#
# random_string, not random_password: the Mongo password is embedded in a
# connection URI and special characters would require percent-encoding.
# The API keys are 48 characters, comfortably over the 32-character minimum
# that ApiSecurityStartup enforces.
###############################################################################

resource "random_string" "mongo_password" {
  length  = 32
  special = false
  numeric = true
  upper   = true
  lower   = true
}

resource "random_string" "password_hash_key" {
  length  = 48
  special = false
}

resource "random_string" "backend_api_secret" {
  length  = 48
  special = false
}

resource "random_string" "frontend_api_secret" {
  length  = 48
  special = false
}

locals {
  mongo_password      = coalesce(var.mongo_admin_password, random_string.mongo_password.result)
  password_hash_key   = coalesce(var.password_hash_key, random_string.password_hash_key.result)
  backend_api_secret  = coalesce(var.backend_api_secret, random_string.backend_api_secret.result)
  frontend_api_secret = coalesce(var.frontend_api_secret, random_string.frontend_api_secret.result)
}
