# Staging. Same module, different inputs - this is the point of the split.
# Configured mid-install: warm replica and installer open, so a fresh
# environment can be seeded without the /install POST timing out.
module "grandnode" {
  source = "../../modules/grandnode"

  environment = "staging"
  location    = "southindia"
  name_prefix = "grandnode"

  image_repository = "grandnode"
  image_tag        = "2"

  mongo_compute_tier      = "Free"
  mongo_storage_gb        = 32
  mongo_high_availability = false

  enable_installer = true
  min_replicas     = 1
  max_replicas     = 1
  cpu              = 1.0
  memory           = "2Gi"

  enable_redis = false

  enable_persistent_volumes = false
  volumes_seeded            = false

  # Cheaper telemetry - staging does not need the same retention.
  log_retention_days       = 30
  log_daily_quota_gb       = 0.2
  appinsights_daily_cap_gb = 0.2

  tags = { owner = "platform", ephemeral = "true" }
}
