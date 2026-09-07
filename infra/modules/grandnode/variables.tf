###############################################################################
# Subscription and naming
###############################################################################


variable "name_prefix" {
  description = <<-EOT
    Short prefix for every resource name. Lowercase letters and digits only -
    it is used for the container registry and storage account, which do not
    permit hyphens and must be globally unique.
  EOT
  type        = string
  default     = "grandnode"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name_prefix))
    error_message = "name_prefix must be 3-12 characters, lowercase alphanumeric, starting with a letter."
  }
}

variable "environment" {
  description = "Environment discriminator appended to resource names (dev, test, prod)."
  type        = string
  default     = "prod"

  validation {
    condition     = can(regex("^[a-z0-9]{2,8}$", var.environment))
    error_message = "environment must be 2-8 lowercase alphanumeric characters."
  }
}

variable "location" {
  description = <<-EOT
    Azure region. Keep the app, database and storage in the SAME region -
    cross-region database latency dominates every request.

    Not westeurope: it is capacity-restricted for new subscriptions. Azure still
    lists it as a supported location for both Microsoft.App and
    Microsoft.DocumentDB/mongoClusters, so a plan will look fine and the apply
    will fail. Regions confirmed to support both services:
      southindia, centralindia, swedencentral, germanywestcentral, uksouth,
      ukwest, northeurope, francecentral, switzerlandnorth, italynorth,
      polandcentral, spaincentral, norwayeast
  EOT
  type        = string
  default     = "southindia"
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default     = {}
}

###############################################################################
# Container image
###############################################################################

variable "image_repository" {
  description = "Repository name inside the container registry."
  type        = string
  default     = "grandnode"
}

variable "image_tag" {
  description = <<-EOT
    Image tag to deploy. Bump this for each release rather than reusing "latest",
    so a rollback is a one-line change. The image must already be present in the
    registry - build it with:
      az acr build -r <registry> -t grandnode:<tag> -f Dockerfile .
  EOT
  type        = string
  default     = "1"
}

###############################################################################
# Cosmos DB for MongoDB vCore
###############################################################################

variable "mongo_admin_username" {
  description = "Administrator username for the Mongo cluster."
  type        = string
  default     = "grandnodeadmin"
}

variable "mongo_admin_password" {
  description = "Administrator password. Leave null to generate one and store it in state."
  type        = string
  default     = null
  sensitive   = true
}

variable "mongo_database_name" {
  description = "Database name within the cluster. Appended to the connection string path."
  type        = string
  default     = "grandnode"
}

variable "mongo_compute_tier" {
  description = <<-EOT
    vCore compute tier. "Free" is the no-cost tier - shared compute, capped at
    32 GB, one free cluster per subscription, no HA. Fine for evaluation and
    development; move to M10/M20/M30 before carrying real traffic.
  EOT
  type        = string
  default     = "Free"
}

variable "mongo_storage_gb" {
  description = "Cluster storage in GB. The Free tier is capped at 32."
  type        = number
  default     = 32
}

variable "mongo_shard_count" {
  description = "Number of shards. Start at 1."
  type        = number
  default     = 1
}

variable "mongo_high_availability" {
  description = "Enable HA (doubles cost). Recommended for production."
  type        = bool
  default     = false
}

variable "mongo_server_version" {
  description = "MongoDB server version. GrandNode requires 4.0 or later."
  type        = string
  default     = "7.0"
}

variable "mongo_allow_azure_services" {
  description = <<-EOT
    Create the 0.0.0.0-0.0.0.0 firewall rule that permits access from Azure
    services. Convenient for a first deployment; replace with a private endpoint
    before going live.
  EOT
  type        = bool
  default     = true
}

###############################################################################
# Container app sizing
###############################################################################

variable "cpu" {
  description = <<-EOT
    vCPU per replica. Container Apps requires a 1:2 CPU:memory ratio, so this
    pairs with memory: 0.25/0.5Gi, 0.5/1Gi, 0.75/1.5Gi, 1.0/2Gi.
    0.5 keeps consumption inside the monthly free grant; raise it if startup is
    slow or requests queue.
  EOT
  type        = number
  default     = 0.5
}

variable "memory" {
  description = "Memory per replica. Must be exactly 2x cpu."
  type        = string
  default     = "1Gi"
}

variable "min_replicas" {
  description = <<-EOT
    Minimum replicas. 0 scales to zero when idle, which is what keeps a free
    subscription at no cost - but see two consequences:

      1. The first request after an idle period pays a cold start. For this
         application that is slow: it loads every plugin and module assembly
         at startup.
      2. Background work stops while scaled to zero. Scheduled tasks run as
         hosted services inside the web host (Grand.Web's Program.cs calls
         RegisterTasks), so queued email, auction expiry and order cancellation
         only progress when an instance is running.

    Set to 1 for anything resembling production.
  EOT
  type        = number
  default     = 0
}

variable "max_replicas" {
  description = <<-EOT
    Maximum replicas. Leave at 1 until Redis is enabled: the application cache is
    per-instance (RedisMessageCacheManager extends MemoryCacheBase and uses Redis
    only as an invalidation bus), so extra replicas without it serve stale data.
  EOT
  type        = number
  default     = 1
}

variable "http_concurrency" {
  description = "Concurrent requests per replica before scaling out."
  type        = number
  default     = 50
}

###############################################################################
# Redis - required for more than one replica
###############################################################################

variable "log_retention_days" {
  description = "Log Analytics retention. 30 days is the free allowance; beyond that is billed."
  type        = number
  default     = 30
}

variable "log_daily_quota_gb" {
  description = <<-EOT
    Hard cap on Log Analytics ingestion per day, in GB. The free grant is 5 GB
    per month, so 0.5/day keeps you inside it. Ingestion stops for the rest of
    the day when the cap is hit - a spending guard, not a sampling control.
    Set to -1 for no cap.
  EOT
  type        = number
  default     = 0.5
}

variable "appinsights_daily_cap_gb" {
  description = "Hard cap on Application Insights ingestion per day, in GB."
  type        = number
  default     = 0.5
}

variable "appinsights_sampling_percentage" {
  description = "Telemetry sampling. Lower means less ingestion and less cost, at the price of fidelity."
  type        = number
  default     = 100
}

variable "scheduled_task_jobs" {
  description = <<-EOT
    Scheduled tasks to run as Container Apps Jobs, keyed by the task name.

    The key must exactly equal the ScheduleTaskName stored in the database, which
    is also the DI registration key - the runner resolves the task by it, and a
    mismatch means the job runs and finds nothing to do.

    Seeded task names:
      "Send emails", "Clear cache", "Generate sitemap XML file", "Delete guests",
      "Update currency exchange rates", "End of the auctions",
      "Cancel unpaid and pending orders", "Apply carrier shipment events"

    Empty (the default) creates no jobs, which is correct when min_replicas >= 1
    and the in-process loop is already running everything.
  EOT
  type = map(object({
    cron            = string
    timeout_seconds = optional(number, 600)
    retry_limit     = optional(number, 1)
    cpu             = optional(number, 0.5)
    memory          = optional(string, "1Gi")
  }))
  default = {}
}

variable "enable_lakehouse" {
  description = <<-EOT
    Create bronze/silver/gold containers on the storage account and let the
    application append raw domain events to bronze.

    Only bronze is produced here. Silver and gold are transformations that
    require a query engine (Fabric, Databricks, Synapse) reading the bronze
    container - no application code can produce them.
  EOT
  type        = bool
  default     = false
}

variable "enable_redis" {
  description = "Provision Azure Cache for Redis and enable cross-replica cache invalidation."
  type        = bool
  default     = false
}

variable "redis_sku" {
  description = "Redis SKU: Basic, Standard or Premium. Basic has no SLA - use Standard for production."
  type        = string
  default     = "Basic"
}

variable "redis_capacity" {
  description = "Redis capacity (0 = 250MB for Basic/Standard)."
  type        = number
  default     = 0
}

###############################################################################
# Application secrets - generated when not supplied
###############################################################################

variable "password_hash_key" {
  description = <<-EOT
    Server-side pepper mixed into PBKDF2 password hashes. SET THIS ONCE, BEFORE
    THE FIRST CUSTOMER REGISTERS - changing it later invalidates every existing
    password hash. Leave null to generate.
  EOT
  type        = string
  default     = null
  sensitive   = true
}

variable "backend_api_secret" {
  description = "JWT signing key for the admin API. Minimum 32 characters. Leave null to generate."
  type        = string
  default     = null
  sensitive   = true
}

variable "frontend_api_secret" {
  description = "JWT signing key for the storefront API. Minimum 32 characters. Leave null to generate."
  type        = string
  default     = null
  sensitive   = true
}

variable "enable_api_module" {
  description = "Enable Grand.Module.Api. When true the two API secret keys are enforced at startup."
  type        = bool
  default     = false
}

variable "enable_persistent_volumes" {
  description = <<-EOT
    Mount Azure Files at /app/App_Data and /app/wwwroot/assets/images so plugin
    install state and file-manager uploads survive a restart.

    Two-phase: apply once with this false to create the shares, seed them from
    the image's own contents, then set true. A mount over an unseeded App_Data
    hides appsettings.json and the host will not start. See infra/README.md.

    Azure Files is SMB and slow for many small files - keep
    Extensions__PluginShadowCopy off when this is on.
  EOT
  type        = bool
  default     = false
}

variable "volumes_seeded" {
  description = <<-EOT
    Confirms the Azure Files shares have been seeded from the image's own
    contents before the mounts are enabled. Purely a guard - it configures
    nothing. Verify with:

      az storage file exists --account-name <sa> --share-name appdata \
        --path appsettings.json --query exists

    then set this true.
  EOT
  type        = bool
  default     = false
}

variable "volume_quota_gb" {
  description = "Size of each Azure Files share, in GB."
  type        = number
  default     = 5
}

variable "installed_plugins" {
  description = <<-EOT
    Comma-separated list of plugin SYSTEM names to treat as installed, e.g.
    "Payments.CashOnDelivery,Shipping.ByWeight,Theme.Modern".

    Set this for any container deployment. Installing a plugin from the admin
    panel writes App_Data/InstalledPlugins.cfg and then calls
    IHostApplicationLifetime.StopApplication() (PluginController.cs:159) - which
    in a container terminates the container, discarding the file it just wrote.
    PluginManager.Load prefers this configuration value and only falls back to
    the file, so listing plugins here is what makes an installation stick.

    System names differ from folder names: Authentication.Facebook ships as
    ExternalAuth.Facebook, ExchangeRate.McExchange as
    CurrencyExchange.MoneyConverter, Shipping.FixedRateShipping as
    Shipping.FixedRate.

    Leave empty to fall back to the file (single-instance, persistent-volume
    deployments only). Only list plugins whose Install() has already run, or
    they are activated without their settings and localization resources.
  EOT
  type        = string
  default     = ""
}

variable "enable_installer" {
  description = <<-EOT
    Leave true for the first apply so /install is reachable, then set to false
    and re-apply. The installer intercepts every path when it believes the
    database is empty.
  EOT
  type        = bool
  default     = true
}
