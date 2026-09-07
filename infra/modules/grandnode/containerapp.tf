###############################################################################
# Container Apps environment and the GrandNode application
###############################################################################

resource "azurerm_container_app_environment" "this" {
  name                       = "cae-${local.suffix}"
  resource_group_name        = azurerm_resource_group.this.name
  location                   = azurerm_resource_group.this.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  tags                       = local.tags
}

locals {
  # Every setting the application needs, as environment variables. .NET maps a
  # double underscore to one level of JSON nesting, and AddEnvironmentVariables()
  # runs after App_Data/appsettings.json loads, so these win.
  plain_env = {
    # Behind Container Apps ingress TLS terminates at the proxy. Without
    # UseForwardedHeaders the app sees plain HTTP and emits wrong redirects
    # and cookie flags.
    Security__UseForwardedHeaders = "true"

    # UseForwardedHeaders alone is NOT sufficient behind Container Apps ingress.
    # UseGrandForwardedHeaders (ApplicationBuilderExtensions.cs) sets
    # ForwardedHeaders but never clears KnownNetworks/KnownProxies, and ASP.NET
    # Core's default trusts only loopback. Container Apps forwards from the pod
    # network (100.100.x.x), so X-Forwarded-Proto is discarded, Request.Scheme
    # stays "http", and the antiforgery system throws as soon as
    # CookieSecurePolicyAlways is on:
    #   "AntiforgeryOptions.Cookie.SecurePolicy = Always, but the current
    #    request is not an SSL request."
    # ForceUseHTTPS rewrites the scheme unconditionally - the escape hatch
    # App_Data/appsettings.json documents for proxies that cannot be trusted
    # by address.
    Security__ForceUseHTTPS            = "true"
    Security__CookieSecurePolicyAlways = "true"
    Security__UseHsts                  = "true"
    Application__DisplayFullErrorStack = "false"

    # Product images to blob storage, so they survive a restart. The endpoint
    # must keep its trailing slash - it is concatenated with the container name.
    Azure__AzureBlobStorageContainerName = azurerm_storage_container.media.name
    Azure__AzureBlobStorageEndPoint      = local.blob_endpoint

    # Data-protection key ring to blob storage, so a restart does not sign
    # every user out and replicas share one ring.
    Azure__PersistKeysToAzureBlobStorage = "true"
    Azure__DataProtectionContainerName   = azurerm_storage_container.dpkeys.name
    Azure__DataProtectionBlobName        = "keys.xml"

    "FeatureManagement__Grand.Module.Api"       = tostring(var.enable_api_module)
    "FeatureManagement__Grand.Module.Installer" = tostring(var.enable_installer)
  }

  # Only emit the key when set - an empty value would make PluginManager fall
  # back to the (ephemeral) file, which is the behaviour we are overriding.
  plugins_env = var.installed_plugins != "" ? {
    Extensions__InstalledPlugins = var.installed_plugins
  } : {}

  redis_env = var.enable_redis ? {
    Redis__RedisPubSubEnabled = "true"
    Redis__RedisPubSubChannel = "grandnode-cache"
  } : {}

  # name -> secret name, for env entries sourced from a secret
  secret_env = merge({
    ConnectionStrings__Mongodb                         = "mongo-connection-string"
    ApplicationInsights__ConnectionString              = "appinsights-connection-string"
    Security__PasswordHashKey                          = "password-hash-key"
    BackendAPI__SecretKey                              = "backend-api-secret"
    FrontendAPI__SecretKey                             = "frontend-api-secret"
    Azure__AzureBlobStorageConnectionString            = "storage-connection-string"
    Azure__PersistKeysAzureBlobStorageConnectionString = "storage-connection-string"
    }, var.enable_redis ? {
    Redis__RedisPubSubConnectionString = "redis-connection-string"
  } : {})
}

resource "azurerm_container_app" "this" {
  name                         = "ca-${local.suffix}"
  resource_group_name          = azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"
  tags                         = local.tags

  secret {
    name  = "mongo-connection-string"
    value = local.mongo_connection_string
  }

  secret {
    name  = "storage-connection-string"
    value = azurerm_storage_account.this.primary_connection_string
  }

  secret {
    name  = "appinsights-connection-string"
    value = azurerm_application_insights.this.connection_string
  }

  secret {
    name  = "password-hash-key"
    value = local.password_hash_key
  }

  secret {
    name  = "backend-api-secret"
    value = local.backend_api_secret
  }

  secret {
    name  = "frontend-api-secret"
    value = local.frontend_api_secret
  }

  secret {
    name  = "registry-password"
    value = azurerm_container_registry.this.admin_password
  }

  dynamic "secret" {
    for_each = var.enable_redis ? [1] : []
    content {
      name  = "redis-connection-string"
      value = local.redis_connection_string
    }
  }

  registry {
    server               = azurerm_container_registry.this.login_server
    username             = azurerm_container_registry.this.admin_username
    password_secret_name = "registry-password"
  }

  ingress {
    external_enabled = true
    # Matches EXPOSE 8080 in the Dockerfile. The aspnet base image defaults
    # ASPNETCORE_HTTP_PORTS to 8080.
    target_port = 8080
    transport   = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    # storage_type defaults to EmptyDir, which is still per-replica and still
    # ephemeral - AzureFile is what actually persists.
    dynamic "volume" {
      for_each = var.enable_persistent_volumes ? local.container_volumes : {}
      content {
        name         = volume.key
        storage_name = azurerm_container_app_environment_storage.this[volume.key].name
        storage_type = "AzureFile"
      }
    }

    container {
      name   = "grandnode"
      image  = "${azurerm_container_registry.this.login_server}/${var.image_repository}:${var.image_tag}"
      cpu    = var.cpu
      memory = var.memory

      dynamic "env" {
        for_each = merge(local.plain_env, local.redis_env, local.plugins_env)
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.secret_env
        content {
          name        = env.key
          secret_name = env.value
        }
      }

      # Gates liveness and readiness until the app has finished starting. This
      # application loads every plugin and module assembly at boot, which on a
      # small vCPU allocation takes far longer than a liveness initial_delay
      # comfortably covers - without this, a slow cold start looks like a
      # liveness failure and the container is killed and retried forever.
      # 30 x 10s allows five minutes to start.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 10
        failure_count_threshold = 30
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        interval_seconds        = 30
        failure_count_threshold = 3
      }

      # Deliberately /health/live, NOT /health/ready.
      #
      # /health/ready runs StartupHealthCheck, which reports on
      # DataSettingsManager.DatabaseIsInstalled(). That value is cached, and
      # InstallController calls ResetCache() on BOTH the success and failure path
      # of installation - and ResetCache() can only force the flag to false,
      # never back to true, without a new process (DataSettingsManager.cs).
      # Wiring the orchestrator's readiness probe to it therefore takes the
      # replica permanently out of rotation the moment anyone runs the installer:
      # every request times out, it never recovers, and it looks like a hang.
      # StartupHealthCheck's own remarks acknowledge the latch.
      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        interval_seconds        = 10
        failure_count_threshold = 3
      }

      dynamic "volume_mounts" {
        for_each = var.enable_persistent_volumes ? local.container_volumes : {}
        content {
          name = volume_mounts.key
          path = volume_mounts.value
        }
      }
    }

    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = tostring(var.http_concurrency)
    }
  }

  depends_on = [
    # The key-ring container must exist before the app boots, or data
    # protection silently falls back to the ephemeral container filesystem.
    azurerm_storage_container.dpkeys,
    azurerm_storage_container.media,
  ]
}

# The /install form submission runs the whole seeding routine inside one HTTP
# request. With min_replicas = 0 the POST has to cold-start the container first,
# and the combined cold start plus seeding exceeds the ingress timeout - the
# browser reports a stream timeout and the request never reaches the app at all.
# Run the installer with at least one warm replica, then scale back to zero.
check "installer_needs_a_warm_replica" {
  assert {
    condition     = !var.enable_installer || var.min_replicas >= 1
    error_message = "enable_installer = true needs min_replicas >= 1, or the /install POST times out during cold start. Set min_replicas = 1 to install, then back to 0."
  }
}

# An Azure Files mount replaces the directory the image ships. /app/App_Data
# carries appsettings.json, which the host reads at startup and cannot boot
# without, plus Resources/ and UrlRewrite.xml. Seed the shares before enabling
# the mounts - see infra/README.md.
check "volumes_must_be_seeded_first" {
  assert {
    condition     = !var.enable_persistent_volumes || var.volumes_seeded
    error_message = "enable_persistent_volumes = true requires volumes_seeded = true, confirming the appdata share contains appsettings.json. Mounting an unseeded share stops the app booting."
  }
}

# Background work has to run somewhere. Scheduled tasks are BackgroundService loops
# inside the web host, so at min_replicas = 0 they only advance while traffic happens
# to be keeping an instance alive - queued email, unpaid-order expiry and the carrier
# outbox all stall silently. Either keep one replica warm, or run the tasks as jobs.
check "background_work_has_a_home" {
  assert {
    condition     = var.min_replicas >= 1 || length(var.scheduled_task_jobs) > 0
    error_message = "min_replicas = 0 stops all scheduled tasks: they are hosted in the web process. Set min_replicas = 1, or define scheduled_task_jobs to run them as Container Apps Jobs."
  }
}
