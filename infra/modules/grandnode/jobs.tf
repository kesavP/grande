###############################################################################
# Scheduled tasks as Container Apps Jobs
#
# GrandNode hosts its scheduled tasks as BackgroundService loops inside the web
# host, so they only advance while a web instance is running. With
# min_replicas = 0 an idle deployment silently stops sending queued email,
# expiring unpaid orders, ending auctions and draining the carrier event outbox -
# and nothing reports that it has stopped.
#
# A job runs the same image with "--run-task <name>", executes one task, and
# exits. Billed only while running, so this keeps scale-to-zero economics while
# giving each task its own cadence, its own retry limit, and an exit code a
# scheduler can alert on.
#
# Safe to run alongside a web replica: ScheduleTaskService.TryClaimTaskRun does an
# atomic compare-and-set on LastStartUtc, so whichever process claims a run first
# executes it and the other stands down.
#
# The job also honours the Enabled flag on the ScheduleTask row, so disabling a
# task in the admin panel stops it here too rather than only in the web host.
###############################################################################

resource "azurerm_container_app_job" "task" {
  for_each = var.scheduled_task_jobs

  name                         = "caj-${substr(replace(lower(each.key), "/[^a-z0-9]+/", "-"), 0, 20)}-${var.environment}"
  resource_group_name          = azurerm_resource_group.this.name
  location                     = azurerm_resource_group.this.location
  container_app_environment_id = azurerm_container_app_environment.this.id

  #a task that overruns this is killed; generous enough for a large email queue
  replica_timeout_in_seconds = each.value.timeout_seconds
  replica_retry_limit        = each.value.retry_limit

  schedule_trigger_config {
    cron_expression = each.value.cron
    #one replica per fire. Parallel replicas would contend for the same lease and
    #all but one would immediately stand down - work, but pointless billing.
    parallelism              = 1
    replica_completion_count = 1
  }

  registry {
    server               = azurerm_container_registry.this.login_server
    username             = azurerm_container_registry.this.admin_username
    password_secret_name = "registry-password"
  }

  #jobs do not share the container app's secrets, so every one it needs is repeated here
  secret {
    name  = "registry-password"
    value = azurerm_container_registry.this.admin_password
  }
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

  template {
    container {
      name   = "task"
      image  = "${azurerm_container_registry.this.login_server}/${var.image_repository}:${var.image_tag}"
      cpu    = each.value.cpu
      memory = each.value.memory

      #the entry point that makes this a job rather than a web host
      command = ["dotnet", "Grand.Web.dll", "--run-task", each.key]

      env {
        name        = "ConnectionStrings__Mongodb"
        secret_name = "mongo-connection-string"
      }
      env {
        name        = "ApplicationInsights__ConnectionString"
        secret_name = "appinsights-connection-string"
      }
      env {
        name        = "Security__PasswordHashKey"
        secret_name = "password-hash-key"
      }
      env {
        name        = "Azure__AzureBlobStorageConnectionString"
        secret_name = "storage-connection-string"
      }
      env {
        name  = "Azure__AzureBlobStorageContainerName"
        value = azurerm_storage_container.media.name
      }
      env {
        name  = "Azure__AzureBlobStorageEndPoint"
        value = local.blob_endpoint
      }

      #migrations must run in exactly one place and the storefront owns them - a job
      #racing the web host on startup has no cross-process lock to arbitrate
      env {
        name  = "FeatureManagement__Grand.Module.Migration"
        value = "false"
      }
      env {
        name  = "FeatureManagement__Grand.Module.Installer"
        value = "false"
      }

      dynamic "env" {
        for_each = var.installed_plugins != "" ? [1] : []
        content {
          name  = "Extensions__InstalledPlugins"
          value = var.installed_plugins
        }
      }

      dynamic "env" {
        for_each = var.enable_lakehouse ? [1] : []
        content {
          name  = "Azure__LakeBronzeContainerName"
          value = "bronze"
        }
      }
    }
  }

  tags = local.tags
}
