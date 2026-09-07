###############################################################################
# Log Analytics and Application Insights
#
# Setting ApplicationInsights__ConnectionString activates the OpenTelemetry
# exporter that AddServiceDefaults() already wires into every host - request
# durations, dependency calls and failure rates, with no code change. Configure
# it on the first deployment, not after a performance problem.
###############################################################################

resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${local.suffix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days

  # Spending guard: ingestion stops for the rest of the day when this is hit.
  daily_quota_gb = var.log_daily_quota_gb

  tags = local.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${local.suffix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"

  # Provider defaults are 90 days and a 100 GB/day cap - both bill well past
  # the free grant on a busy day.
  retention_in_days    = var.log_retention_days
  daily_data_cap_in_gb = var.appinsights_daily_cap_gb
  sampling_percentage  = var.appinsights_sampling_percentage

  tags = local.tags
}
