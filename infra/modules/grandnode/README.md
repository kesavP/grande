# GrandNode2 — Azure infrastructure (Terraform)

Provisions everything needed to run GrandNode2 on Azure Container Apps with
**Cosmos DB for MongoDB vCore**. Companion to section 8 of [`../techspec.md`](../techspec.md),
which explains *why* each setting is what it is.

## What it creates

| Resource | Purpose |
|---|---|
| `azurerm_resource_group` | holds everything |
| `azurerm_container_registry` | stores the application image |
| `azurerm_mongo_cluster` | Cosmos DB for MongoDB **vCore** |
| `azurerm_mongo_cluster_firewall_rule` | optional allow-from-Azure rule |
| `azurerm_storage_account` + 2 containers | `media` (product images) and `dpkeys` (data-protection key ring) |
| `azurerm_log_analytics_workspace` | container logs |
| `azurerm_application_insights` | activates the OpenTelemetry export already wired into the app |
| `azurerm_container_app_environment` | Container Apps environment |
| `azurerm_container_app` | the application, with secrets, env vars and health probes |
| `azurerm_redis_cache` | optional; **required** above one replica |

## Why vCore and not the RU-based Mongo API

The RU offering is a compatibility layer over a different storage engine.
GrandNode relies on genuine MongoDB behaviour — GridFS for file storage, the
array update operators exposed by `IRepository<T>` (`AddToCollectionField`,
`UpdateCollectionFieldItem`), and aggregation — which that layer implements
incompletely.

## Cost posture

Defaults are tuned for a **free / low-credit subscription**:

| Resource | Default | Cost |
|---|---|---|
| Mongo vCore | `compute_tier = "Free"`, 32 GB, no HA | free — one free cluster per subscription |
| Container app | 0.5 vCPU / 1 GiB, `min_replicas = 0` | scales to zero; the monthly free grant covers light use |
| Log Analytics | 30-day retention, 0.5 GB/day cap | free grant is 5 GB/month |
| Application Insights | 30-day retention, 0.5 GB/day cap | billed through the workspace above |
| Redis | `enable_redis = false` | not created |
| Storage | Standard LRS | pennies at this size |
| Container registry | Basic | **the one unavoidable charge — ACR has no free tier** |

Two consequences of `min_replicas = 0` that are easy to miss:

1. **Cold starts are slow.** The app loads every plugin and module assembly at
   startup, so the first request after an idle period waits.
2. **Background work pauses.** Scheduled tasks run as hosted services inside the
   web host (`Grand.Web/Program.cs` calls `RegisterTasks`), so queued email,
   auction expiry and unpaid-order cancellation only progress while an instance
   is running. If any of those matter, set `min_replicas = 1`.

To move to production, raise `mongo_compute_tier` to M30 with
`mongo_high_availability = true`, set `min_replicas = 1`, `cpu = 1.0` /
`memory = "2Gi"`, and turn on Redis before raising `max_replicas`.

**Free-tier availability is confirmed only at apply.** No Azure API exposes which
compute tiers are offered in a given region, so if `Free` is unavailable in
South India the apply will say so — fall back to `M10` or try Central India.

## Prerequisites

- Terraform >= 1.9, Azure CLI, and `az login` completed.
- **Contributor** on the target subscription.
- Resource providers registered: `Microsoft.App`, `Microsoft.ContainerRegistry`,
  `Microsoft.OperationalInsights`, `Microsoft.Storage`, `Microsoft.Cache`,
  `Microsoft.DocumentDB`. See `../techspec.md` §8.1.

## Usage

```bash
cd infra
cp terraform.tfvars.example terraform.tfvars     # edit subscription_id at minimum
terraform init
terraform plan
terraform apply
```

The container app is created before any image exists, so its first revision
will fail to pull. That is expected. Build the image, then force a new revision:

```bash
cd ..                                            # repository root - build context
az acr build -r $(terraform -chdir=infra output -raw container_registry_name) \
  -t grandnode:1 -f Dockerfile .

terraform -chdir=infra apply -replace=azurerm_container_app.this
```

Then seed the database:

```bash
terraform -chdir=infra output install_url        # open it in a browser
```

Create the administrator account. The MongoDB fields on that form are **ignored**
— `InstallController` prefers the configured `ConnectionStrings:Mongodb`. Once
installed, close the installer:

```bash
# in terraform.tfvars
enable_installer = false
```

```bash
terraform apply
```

## Scaling out

`max_replicas > 1` requires `enable_redis = true`. A `check` block enforces this
and the plan will tell you if you forget.

The reason is not performance but correctness: the application cache is
per-instance. `RedisMessageCacheManager` extends `MemoryCacheBase` and publishes
only *invalidation messages* over Redis — nothing is stored there. Without it, a
write on one replica leaves the others serving stale data for up to
`Cache:DefaultCacheTimeMinutes` (default 60).

Note what horizontal scale does and does not buy you: each replica keeps its own
cache, so N replicas cold-miss independently and MongoDB sees roughly N times the
read load for the same misses. Scale-out adds render capacity; it does not
relieve the database.

## Secrets and state

Passwords and keys are generated when not supplied, which means **they live in
the state file in plaintext**. Two consequences:

1. Use the remote backend (`backend.tf.example`) with a private, access-controlled
   container. `*.tfstate` and `*.tfvars` are gitignored here, but that only helps
   locally.
2. To keep secrets out of state entirely, supply them yourself from a secret
   manager via `-var` or environment variables, and wire Key Vault references
   into the container app instead.

`Security__PasswordHashKey` deserves particular care: it is the pepper mixed into
every PBKDF2 password hash. **Set it once, before the first customer registers.**
Changing it later invalidates every existing password.

## Things this module deliberately does not do

- **Seed the database.** `/install` is an interactive form; that step stays manual.
- **Split the admin panel** into its own container app. The Dockerfile supports it
  (`--target admin`, and `--build-arg INCLUDE_ADMIN=false` for the storefront) —
  see `../techspec.md` §8.10. Worth doing only for ingress isolation.
- **Use managed identity for the registry pull.** It uses admin credentials, which
  make the first apply reliable. To switch: set `admin_enabled = false`, add
  `identity { type = "SystemAssigned" }` to the container app, replace the
  `registry` block's username/password with `identity = "system"`, and add an
  `azurerm_role_assignment` granting `AcrPull` on the registry. That needs
  `Microsoft.Authorization/roleAssignments/write`, and the first apply may need a
  retry while the assignment propagates.
- **Create a private endpoint** for MongoDB. `mongo_allow_azure_services` opens the
  cluster to Azure services, which is fine to start and should be replaced before
  going live.

## Verifying the connection string

The vCore connection URI is constructed in `database.tf` from the documented
format. The provider also exposes what Azure itself reports:

```bash
terraform output -json mongo_connection_strings_from_azure
```

Compare the two on the first apply. If the host suffix differs, trust Azure's and
correct `local.mongo_connection_string`.

## Status

Applied successfully against a live subscription (South India, 2026-09-04) with
`hashicorp/azurerm v4.81.0`: 15 resources, storefront and admin panel both
serving. Confirmed in the process:

- `compute_tier = "Free"` **is** available for Mongo vCore in South India; the
  cluster provisioned in about 6 minutes.
- The constructed vCore connection URI in `database.tf` works as written.
- `az acr build` takes roughly 8.5 minutes. ACR Tasks uses the classic Docker
  builder rather than BuildKit, so it builds *every* stage in the Dockerfile,
  including the admin publish stage the default target does not need.
- `Security__ForceUseHTTPS = "true"` is **required**, not optional — see the
  comment in `containerapp.tf` and section 5.2 of `../techspec.md`.

The container app is created before its image exists, so plan for the two-phase
apply described above.
