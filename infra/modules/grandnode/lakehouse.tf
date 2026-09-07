###############################################################################
# Lakehouse containers - medallion layout on the existing ADLS Gen2 account
#
# The storage account already has hierarchical namespace enabled (storage.tf),
# which is what makes it ADLS Gen2 rather than flat blob: directory rename and
# delete are atomic metadata operations, which every lakehouse engine relies on
# when committing partitions.
#
#   bronze/   raw events exactly as received, append-only, never edited.
#             Written by the application. Partitioned by event date so a reader
#             can prune without scanning the account:
#               bronze/shipment_events/dt=2026-09-07/<batch>.jsonl
#
#   silver/   deduplicated, typed, one row per business event. Produced by a
#             query engine reading bronze - NOT by this application.
#
#   gold/     aggregates shaped for consumption - delivery SLA per carrier,
#             on-time rate per store. Also engine-produced.
#
# Only bronze is written by GrandNode. Silver and gold are transformations that
# need Fabric, Databricks or Synapse; there is no application code that could
# produce them, and pretending otherwise would leave two empty containers.
###############################################################################

locals {
  lake_layers = var.enable_lakehouse ? toset(["bronze", "silver", "gold"]) : toset([])
}

resource "azurerm_storage_container" "lake" {
  for_each = local.lake_layers

  name                  = each.value
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}
