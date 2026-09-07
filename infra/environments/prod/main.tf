# Production. Free-tier steady state: scale-to-zero, Free vCore, no Redis.
#
# Pin the module by tag once it is published separately:
#   source = "github.com/<you>/terraform-azurerm-grandnode?ref=v1.0.0"
module "grandnode" {
  source = "../../modules/grandnode"

  environment = "prod"
  location    = "southindia"
  name_prefix = "grandnode"

  # Tag 2 carries the installer fix (CreateTables no longer skips index
  # creation when collation is empty). Tag 1 installs an unindexed database.
  image_repository = "grandnode"
  image_tag        = "2"

  mongo_compute_tier      = "Free"
  mongo_storage_gb        = 32
  mongo_high_availability = false

  # Steady state. To (re)install, flip to enable_installer = true with
  # min_replicas = 1 / cpu = 1.0 / memory = "2Gi" - the /install POST seeds the
  # database inside one HTTP request and times out on a cold start.
  enable_installer = false
  min_replicas     = 0
  max_replicas     = 1
  cpu              = 0.5
  memory           = "1Gi"

  enable_redis = false

  # Apply once false to create the shares, seed them from the image's own
  # App_Data and wwwroot/assets/images, then set both true.
  enable_persistent_volumes = false
  volumes_seeded            = false

  installed_plugins = ""

  log_daily_quota_gb       = 0.5
  appinsights_daily_cap_gb = 0.5

  tags = { owner = "platform" }
}
