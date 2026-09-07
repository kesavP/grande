###############################################################################
# Container registry
#
# admin_enabled is used so the container app can pull with a username/password
# on the very first apply. A user-assigned identity with an AcrPull role
# assignment is the better long-term posture, but it needs
# Microsoft.Authorization/roleAssignments/write and is prone to a failed first
# apply while the assignment propagates. See infra/README.md to switch.
###############################################################################

resource "azurerm_container_registry" "this" {
  name                = "acr${local.compact_suffix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = "Basic"
  admin_enabled       = true
  tags                = local.tags
}
