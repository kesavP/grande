###############################################################################
# Azure Cache for Redis - optional, REQUIRED above one replica
#
# The application cache is per-instance: RedisMessageCacheManager extends
# MemoryCacheBase and publishes only invalidation messages over Redis; nothing
# is stored there. Without it, a write on one replica leaves the others serving
# stale data for up to Cache:DefaultCacheTimeMinutes (default 60). That makes
# this a correctness requirement for multi-replica, not a tuning option.
###############################################################################

resource "azurerm_redis_cache" "this" {
  count = var.enable_redis ? 1 : 0

  name                 = "redis-${local.suffix}"
  resource_group_name  = azurerm_resource_group.this.name
  location             = azurerm_resource_group.this.location
  capacity             = var.redis_capacity
  family               = var.redis_sku == "Premium" ? "P" : "C"
  sku_name             = var.redis_sku
  non_ssl_port_enabled = false
  minimum_tls_version  = "1.2"
  tags                 = local.tags
}

locals {
  redis_connection_string = var.enable_redis ? join("", [
    azurerm_redis_cache.this[0].hostname,
    ":",
    tostring(azurerm_redis_cache.this[0].ssl_port),
    ",password=",
    azurerm_redis_cache.this[0].primary_access_key,
    ",ssl=True,abortConnect=False"
  ]) : ""
}

check "redis_required_for_scale_out" {
  assert {
    condition     = var.max_replicas <= 1 || var.enable_redis
    error_message = "max_replicas > 1 requires enable_redis = true, or replicas will serve stale cached data to customers."
  }
}
