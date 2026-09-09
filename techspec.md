# GrandNode2 — Docker & Azure Deployment Technical Specification

This document covers building the Docker images, generating the API secret keys, running
the application in Docker, deploying it to Azure, and publishing the storefront's frontend
assets independently of the application.

- [1. What is being deployed](#1-what-is-being-deployed)
- [2. Prerequisites](#2-prerequisites)
- [3. Building the images](#3-building-the-images)
  - [3.1 All-in-one image](#31-all-in-one-image)
  - [3.2 Storefront-only image](#32-storefront-only-image)
  - [3.3 Admin-only image](#33-admin-only-image)
  - [3.4 Build arguments](#34-build-arguments)
- [4. Generating the API secret keys](#4-generating-the-api-secret-keys)
  - [4.1 When they are required](#41-when-they-are-required)
  - [4.2 Why 32 characters](#42-why-32-characters)
  - [4.3 Generating a key](#43-generating-a-key)
  - [4.4 Rules for the value](#44-rules-for-the-value)
- [5. Configuration reference](#5-configuration-reference)
  - [5.1 Core settings](#51-core-settings)
  - [5.2 Behind a reverse proxy or cloud ingress](#52-behind-a-reverse-proxy-or-cloud-ingress)
  - [5.3 Settings that must be identical on every host sharing one database](#53-settings-that-must-be-identical-on-every-host-sharing-one-database)
  - [5.4 Multi-instance and scale-out](#54-multi-instance-and-scale-out)
- [6. Running in Docker — single container](#6-running-in-docker--single-container)
  - [6.1 Network and database](#61-network-and-database)
  - [6.2 Application](#62-application)
  - [6.3 First-run installation](#63-first-run-installation)
  - [6.4 Skipping the installer](#64-skipping-the-installer)
- [7. Running in Docker — split storefront / admin](#7-running-in-docker--split-storefront--admin)
  - [7.1 Storefront](#71-storefront)
  - [7.2 Admin](#72-admin)
- [8. Deploying to Azure](#8-deploying-to-azure)
  - [8.1 Prerequisites](#81-prerequisites)
  - [8.2 What gets created, and why](#82-what-gets-created-and-why)
  - [8.3 Deploying](#83-deploying)
  - [8.4 Scaling out](#84-scaling-out)
  - [8.5 Scheduled tasks with scale-to-zero](#85-scheduled-tasks-with-scale-to-zero)
  - [8.6 Running and operating scheduled tasks](#86-running-and-operating-scheduled-tasks)
  - [8.7 Optional — split storefront / admin](#87-optional--split-storefront--admin)
  - [8.8 Alternative — App Service](#88-alternative--app-service)
  - [8.9 Custom domain and TLS](#89-custom-domain-and-tls)
  - [8.10 Updating a deployment](#810-updating-a-deployment)
  - [8.11 Troubleshooting](#811-troubleshooting)
  - [8.12 Applying from a pipeline, and keeping the evidence](#812-applying-from-a-pipeline-and-keeping-the-evidence)
  - [8.13 Validating a deployment against the rules](#813-validating-a-deployment-against-the-rules)
  - [8.14 What the Terraform module enforces](#814-what-the-terraform-module-enforces)
- [9. Frontend assets — build, publish, and Subresource Integrity](#9-frontend-assets--build-publish-and-subresource-integrity)
  - [9.1 Building the bundles and the manifest](#91-building-the-bundles-and-the-manifest)
  - [9.2 Provisioning the storage container](#92-provisioning-the-storage-container)
  - [9.3 Publishing a release](#93-publishing-a-release)
  - [9.4 The base URL](#94-the-base-url)
  - [9.5 Wiring it up in the admin panel](#95-wiring-it-up-in-the-admin-panel)
  - [9.6 How a page picks the bundle up](#96-how-a-page-picks-the-bundle-up)
  - [9.7 What SRI actually protects](#97-what-sri-actually-protects)
  - [9.8 Rolling back](#98-rolling-back)
  - [9.9 When it goes wrong](#99-when-it-goes-wrong)
  - [9.10 Restricting where scripts may come from](#910-restricting-where-scripts-may-come-from)
  - [9.11 Limitations](#911-limitations)
- [10. Verification](#10-verification)
- [11. Known constraints](#11-known-constraints)
---

## 1. What is being deployed

GrandNode2 is a **modular monolith**, not a set of microservices. `Grand.Web` project-references
`Grand.Web.Admin`, `Grand.Web.Store` and `Grand.Web.Vendor`, so by default the storefront, admin
panel, store-owner panel and vendor panel are one process over one MongoDB database. Commands and
queries are dispatched in-process through `Grand.Mediator`; there are no service-to-service calls.

The build supports three images:

| Image | Build command | Contents | Entry point |
|---|---|---|---|
| All-in-one (default) | `docker build .` | storefront + admin + store + vendor | `Grand.Web.dll` |
| Storefront only | `docker build --build-arg INCLUDE_ADMIN=false .` | storefront + store + vendor, **no `/admin`** | `Grand.Web.dll` |
| Admin only | `docker build --target admin .` | admin panel only | `Grand.Web.Admin.dll` |

All three listen on **port 8080** and run as the non-root `app` user.

Splitting the admin into its own container is optional. Its main benefit on Azure is that
Container Apps ingress restrictions apply per-app, so a separate admin app can be given internal
ingress or an IP allowlist without a WAF in front. See [section 7](#7-running-in-docker--split-storefront--admin)
and [section 11](#11-known-constraints) for the trade-offs.

---

## 2. Prerequisites

- Docker with BuildKit (default in modern Docker).
- MongoDB **4.0 or later** — a container, a MongoDB Atlas cluster, or Azure Cosmos DB for
  MongoDB **vCore**. The older RU-based Cosmos Mongo API is a compatibility layer on a different
  engine and is not recommended.
- No local .NET SDK is required; the image builds the solution inside the SDK container.

---

## 3. Building the images

Run from the repository root.

### 3.1 All-in-one image

```bash
docker build -t grandnode .
```

This is the unchanged default path — the same image the `docker-image.yml` CI workflow produces.

### 3.2 Storefront-only image

```bash
docker build --build-arg INCLUDE_ADMIN=false -t grandnode-web .
```

`INCLUDE_ADMIN=false` passes `-p:IncludeAdminPanel=false` to the publish, which drops the
`Grand.Web.Admin` project reference. No source in `Grand.Web` references admin types — the
reference exists only so the assembly is present as an MVC application part, which is what maps
the `/admin` routes. Without it, `Grand.Web.Admin.dll` is not in the image and `/admin` returns 404.

### 3.3 Admin-only image

```bash
docker build --target admin -t grandnode-admin .
```

This publishes `Grand.Web.Admin.csproj` with `-p:StandaloneAdmin=true`, which enables runtime
configuration file generation, publishes the admin's own `App_Data/appsettings.json`, and copies
the `Plugins` and `Modules` folders that the plugin projects build into `Grand.Web`.

The Dockerfile uses separate `publish-web` and `publish-admin` stages, so BuildKit builds only the
stages the chosen target depends on — `--target admin` never runs the storefront publish, and the
default build never runs the admin publish.

### 3.4 Build arguments

| Argument | Default | Purpose |
|---|---|---|
| `INCLUDE_ADMIN` | `true` | `false` excludes the admin panel from the storefront image |
| `GIT_COMMIT` | empty | stamped into the assembly as `SourceRevisionId` |
| `GIT_BRANCH` | empty | stamped into the assembly as `GitBranch` |

---

## 4. Generating the API secret keys

### 4.1 When they are required

`BackendAPI:SecretKey` (admin API) and `FrontendAPI:SecretKey` (storefront API) sign and validate
JWTs. **They are only required when the API module is enabled.** Both shipped `appsettings.json`
files set `"FeatureManagement": { "Grand.Module.Api": false }`, and `ModuleLoader.LoadModules`
skips a disabled module entirely — the assembly is never loaded, so the startup validation never
runs. With the default configuration the containers start without these variables.

They become mandatory as soon as you set `FeatureManagement__Grand.Module.Api=true`. At that point
`ApiSecurityStartup` **aborts startup** outside the Development environment if a key is missing,
still set to the shipped placeholder (`your private secret key to use api`), or shorter than 32
characters. In Development it only logs a warning.

Setting them up front is recommended: it costs nothing and means enabling the API later cannot
fail startup.

### 4.2 Why 32 characters

The token is signed with HS256, and the signing key is the raw ASCII bytes of the secret string.
HS256 requires a 256-bit key, so the secret must be at least 32 characters. This is enforced by
`MinSecretLength = 32` in `src/Modules/Grand.Module.Api/Infrastructure/ApiSecurityStartup.cs`.

### 4.3 Generating a key

```bash
# 48 hex characters
openssl rand -hex 24

# or base64
openssl rand -base64 32

# or, without openssl
head -c 32 /dev/urandom | xxd -p -c 32
```

PowerShell:

```powershell
-join ((48..57) + (97..122) | Get-Random -Count 48 | ForEach-Object {[char]$_})
```

Store the value in a secret store (Azure Container Apps secrets, Key Vault, Docker secrets) —
never in `appsettings.json` and never in source control.

### 4.4 Rules for the value

- Generate **one** key per API section, and use the **same** value on every host that shares the
  database. A token signed by one host must validate on another; different keys mean every
  cross-host token is rejected with 401.
- `BackendAPI:SecretKey` and `FrontendAPI:SecretKey` should be **different from each other** —
  they protect different audiences.
- Rotating a key invalidates all outstanding tokens for that API. Clients must re-authenticate.

---

## 5. Configuration reference

Every key in `App_Data/appsettings.json` is overridable by an environment variable. .NET maps a
**double underscore to one level of nesting**, and `AddEnvironmentVariables()` runs after the JSON
file loads, so environment variables win.

```
ConnectionStrings__Mongodb   ->  { "ConnectionStrings": { "Mongodb": ... } }
BackendAPI__SecretKey        ->  { "BackendAPI":        { "SecretKey": ... } }
Security__UseForwardedHeaders->  { "Security":          { "UseForwardedHeaders": ... } }
```

### 5.1 Core settings

| Variable | Notes |
|---|---|
| `ConnectionStrings__Mongodb` | MongoDB connection string. When set, the app treats the database as configured and does not write `App_Data/Settings.cfg`. |
| `BackendAPI__SecretKey` | 32+ chars. Required only when the API module is enabled — see [section 4](#4-generating-the-api-secret-keys). |
| `FrontendAPI__SecretKey` | 32+ chars. Same condition. |
| `Security__PasswordHashKey` | Server-side pepper for PBKDF2 hashes. **Set before going live** — changing it later invalidates every existing password hash. |

### 5.2 Behind a reverse proxy or cloud ingress

| Variable | Recommended | Why |
|---|---|---|
| `Security__UseForwardedHeaders` | `true` | TLS terminates at the proxy; without this the app sees HTTP and produces wrong redirects and cookie flags. |
| `Security__ForceUseHTTPS` | `true` | **Required on Azure Container Apps** — see below. |
| `Security__CookieSecurePolicyAlways` | `true` | Always set the `Secure` cookie flag. |
| `Security__UseHsts` | `true` | Adds HSTS. |
| `Application__DisplayFullErrorStack` | `false` | Do not leak stack traces in production. |

> **`UseForwardedHeaders` alone is not sufficient behind Azure Container Apps.**
> `UseGrandForwardedHeaders` sets `ForwardedHeaders = XForwardedFor | XForwardedProto` but never
> clears `KnownNetworks`/`KnownProxies`
> (`src/Web/Grand.Web.Common/Infrastructure/ApplicationBuilderExtensions.cs:188-190`). ASP.NET
> Core's defaults trust only loopback, and Container Apps' ingress forwards from the pod network
> (`100.100.x.x`), so `X-Forwarded-Proto` is discarded and `Request.Scheme` stays `http`. With
> `CookieSecurePolicyAlways` on, the first form submission then fails with:
>
> ```
> System.InvalidOperationException: The antiforgery system has the configuration value
> AntiforgeryOptions.Cookie.SecurePolicy = Always, but the current request is not an SSL request.
> ```
>
> Every page redirects to `/errorpage.htm` and the cause is invisible without the container logs.
> `Security__ForceUseHTTPS=true` rewrites the scheme unconditionally — the escape hatch
> `appsettings.json` documents for proxies that cannot be trusted by address. Confirmed against a
> live deployment.

### 5.3 Settings that must be identical on every host sharing one database

Mixing these across hosts breaks login resolution — typically the administrator can no longer
sign in.

- `Customer__RegisterCustomersPerStore`
- `Security__CookiePrefix`
- `Security__CookieClaimsIssuer`
- `Security__PasswordHashKey`
- the data-protection key ring (see [section 5.4](#54-multi-instance-and-scale-out))

### 5.4 Multi-instance and scale-out

Three pieces of state are per-container and are lost on restart or invisible to other replicas.

| State | Default location | Fix |
|---|---|---|
| Data-protection keys | `App_Data/DataProtectionKeys` | `Azure__PersistKeysToAzureBlobStorage=true` plus `Azure__PersistKeysAzureBlobStorageConnectionString`, `Azure__DataProtectionContainerName`, `Azure__DataProtectionBlobName` — the blob container must already exist. Without this, every restart signs out all users. |
| Uploaded product images | `wwwroot/assets/images` | `Azure__AzureBlobStorageConnectionString`, `Azure__AzureBlobStorageContainerName`, `Azure__AzureBlobStorageEndPoint` (trailing slash — it is string-concatenated) switches `IPictureService` to `AzurePictureService`. |
| Per-instance memory cache | — | Azure Cache for Redis plus `Redis__RedisPubSubEnabled=true`, `Redis__RedisPubSubConnectionString`, `Redis__RedisPubSubChannel`. |

Scheduled tasks are already safe on multiple instances: `ScheduleTaskService.TryClaimTaskRun`
does an atomic compare-and-set on `LastStartUtc`, so only one instance claims each run.

---

## 6. Running in Docker — single container

### 6.1 Network and database

```bash
docker network create grandnode-net

docker run -d --name grandnode-mongo --network grandnode-net \
  -v grandnode_db:/data/db \
  mongo:7
```

### 6.2 Application

```bash
KEY_BACKEND=$(openssl rand -hex 24)
KEY_FRONTEND=$(openssl rand -hex 24)

docker run -d --name grandnode --network grandnode-net \
  -p 8080:8080 \
  -v grandnode_appdata:/app/App_Data \
  -v grandnode_images:/app/wwwroot/assets/images \
  -e BackendAPI__SecretKey="$KEY_BACKEND" \
  -e FrontendAPI__SecretKey="$KEY_FRONTEND" \
  grandnode
```

The two volumes are not optional for anything but throwaway testing. `App_Data` holds
`Settings.cfg`, `InstalledPlugins.cfg` and the data-protection key ring;
`wwwroot/assets/images` holds uploaded media. Both live on the container filesystem and are
otherwise lost on every recreate.

### 6.3 First-run installation

Open <http://localhost:8080>. The application redirects to `/install`. Provide:

- **MongoDB server name**: `grandnode-mongo` (resolvable over `grandnode-net`)
- **Database name**: `grandnode`
- **Admin email and password** — these become the administrator account. The form pre-fills the
  email with `admin@yourstore.com`; the password field is blank and you choose it.
- Optionally load sample data.

The `admin@yourstore.com` / `123456` pair in `README.md` belongs to the public demo site. It is
not a default on your installation — it works only if you type it yourself.

There is no password-reset CLI. To start over, delete `App_Data/Settings.cfg` inside the container
(or drop the database) and re-run `/install`.

After installation the storefront is at `/` and the admin panel at `/admin`.

### 6.4 Skipping the installer

Setting `ConnectionStrings__Mongodb` makes the app treat the database as already configured, so
the schema and administrator account must already exist. Use this to point a new container at a
database that was installed earlier:

```bash
-e ConnectionStrings__Mongodb="mongodb://grandnode-mongo/grandnode"
```

Once installed, set `FeatureManagement__Grand.Module.Installer=false`.

---

## 7. Running in Docker — split storefront / admin

Both containers are hosts over the **same** database. They are not independent services.

### 7.1 Storefront

```bash
docker run -d --name grandnode-web --network grandnode-net \
  -p 8080:8080 \
  -v grandnode_appdata:/app/App_Data \
  -v grandnode_images:/app/wwwroot/assets/images \
  -e ConnectionStrings__Mongodb="mongodb://grandnode-mongo/grandnode" \
  -e BackendAPI__SecretKey="$KEY_BACKEND" \
  -e FrontendAPI__SecretKey="$KEY_FRONTEND" \
  grandnode-web
```

`/admin` and `/admin/login` return 404 on this image.

### 7.2 Admin

```bash
docker run -d --name grandnode-admin --network grandnode-net \
  -p 8081:8080 \
  -v grandnode_admin_appdata:/app/App_Data \
  -v grandnode_images:/app/wwwroot/assets/images \
  -e ConnectionStrings__Mongodb="mongodb://grandnode-mongo/grandnode" \
  -e BackendAPI__SecretKey="$KEY_BACKEND" \
  -e FrontendAPI__SecretKey="$KEY_FRONTEND" \
  -e Extensions__InstalledPlugins="Widgets.Slider,DiscountRules.Standard,Widgets.GoogleAnalytics,CurrencyExchange.MoneyConverter,Widgets.FacebookPixel,Tax.CountryStateZip,Payments.BrainTree,Tax.FixedRate,Shipping.FixedRate,Payments.CashOnDelivery,Payments.StripeCheckout,Shipping.ShippingPoint,ExternalAuth.Facebook,ExternalAuth.Google,Theme.Modern,Shipping.ByWeight" \
  grandnode-admin
```

Admin panel at <http://localhost:8081/admin>.

`Extensions__InstalledPlugins` is needed because `App_Data/InstalledPlugins.cfg` is written
locally by whichever host installs a plugin. `PluginManager.Load` prefers the config value and
only falls back to the file, so setting the same list on both hosts keeps them in agreement.

The values are plugin **system names**, which differ from folder names:

| Folder | System name |
|---|---|
| `Authentication.Facebook` | `ExternalAuth.Facebook` |
| `Authentication.Google` | `ExternalAuth.Google` |
| `ExchangeRate.McExchange` | `CurrencyExchange.MoneyConverter` |
| `Shipping.FixedRateShipping` | `Shipping.FixedRate` |

Read the authoritative list for your installation with:

```bash
docker exec grandnode-web cat /app/App_Data/InstalledPlugins.cfg
```

No migration or scheduled-task flags are needed on the admin container. Its own
`App_Data/appsettings.json` already ships `Grand.Module.Installer`, `Grand.Module.Migration` and
`Grand.Module.ScheduledTasks` as `false`, so the storefront owns them. Do not enable them there:
migrations have no cross-process lock, so exactly one host must run them.

Mount the same media volume on both containers. `IMediaFileStore` and the elFinder file manager
always write to the local `wwwroot` even when pictures are configured to go to blob storage, so
without a shared mount, files uploaded through admin are invisible to the storefront.

---

## 8. Deploying to Azure

A complete walkthrough, from an empty subscription to a running store. Azure Container Apps is
the recommended target; [section 8.8](#88-alternative--app-service) covers App Service.

> **Two paths to the same result.** This section is the imperative `az` CLI walkthrough — useful
> for understanding exactly what gets created, and for a one-off environment. For anything
> repeatable, use the Terraform in [`infra/`](infra/README.md):
>
> ```
> infra/
> ├── modules/grandnode/     every resource; no provider block, no subscription_id
> └── environments/
>     ├── prod/              provider + backend + state + a module call
>     └── staging/
> ```
>
> Each environment is a separate root with its own state, so an apply in staging cannot touch
> production. Adding an environment is ~20 lines calling the same module.
>
> ```bash
> cd infra/environments/prod
> terraform init && terraform plan && terraform apply
> ```
>
> The explanations below apply to both paths. The module encodes several of them as `check` blocks
> that fail at plan time rather than at apply — see [section 8.14](#814-what-the-terraform-module-enforces).

### 8.1 Prerequisites

**Accounts and permissions**

- An Azure subscription.
- **Contributor** on the subscription, or on a pre-created resource group, plus **User Access
  Administrator** if you intend to use managed identity for the registry pull.
- Azure CLI 2.60 or later: `az version`. Install from <https://aka.ms/azcli>.

**Local tooling**

- Azure CLI, signed in: `az login`
- The Container Apps extension:

  ```bash
  az extension add --name containerapp --upgrade
  az extension add --name application-insights --upgrade
  ```

- **Docker is not required.** `az acr build` builds the image inside Azure from your source.
  Install Docker only if you want to build and test locally first.
- Git, to clone the repository.

**Register resource providers** (once per subscription — silently causes failures if skipped):

```bash
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.ContainerRegistry
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Storage
az provider register --namespace Microsoft.Cache
# only if using Cosmos DB for MongoDB vCore
az provider register --namespace Microsoft.DocumentDB
```

Registration takes a few minutes. Check with
`az provider show -n Microsoft.App --query registrationState`.

**Decisions to make before you start**

| Decision | Options | Guidance |
|---|---|---|
| Region | see note | Put the app, database and storage in the **same** region — cross-region database latency dominates every request. **Not `westeurope`** — see below. |
| MongoDB | Atlas, Cosmos DB for MongoDB **vCore**, or self-hosted | Atlas or vCore. The RU-based Cosmos Mongo API is a compatibility layer on a different engine, not a real MongoDB server. |
| Topology | single container, or split storefront/admin | Start single. Split only for ingress isolation — see [section 8.7](#87-optional--split-storefront--admin). |
| Scale | 1 replica, or many | Start at 1. Multi-replica requires Redis and blob storage first — see [section 8.4](#84-scaling-out). |
| Custom domain | yes/no | Optional, can be added later. |

> **Region availability.** `westeurope` is capacity-restricted for new subscriptions. The trap is
> that it still appears as a supported location for both `Microsoft.App` and
> `Microsoft.DocumentDB/mongoClusters`, so provider queries and `terraform plan` look fine and the
> apply fails. Confirm what your own subscription supports before you start:
>
> ```bash
> az provider show -n Microsoft.App \
>   --query "resourceTypes[?resourceType=='managedEnvironments'].locations | [0]" -o tsv | sort > /tmp/ca.txt
> az provider show -n Microsoft.DocumentDB \
>   --query "resourceTypes[?resourceType=='mongoClusters'].locations | [0]" -o tsv | sort > /tmp/mc.txt
> comm -12 /tmp/ca.txt /tmp/mc.txt
> ```
>
> That gives the intersection of regions offering both services — but not capacity restrictions,
> which are not exposed by any API. European regions supporting both: Sweden Central, Germany West
> Central, UK South, UK West, North Europe, France Central, Switzerland North, Italy North, Poland
> Central, Spain Central, Norway East. India: South India, Central India. These docs default
> to **South India**.

**What this will create**

Resource group, container registry, MongoDB, storage account, Log Analytics workspace,
Application Insights, Container Apps environment, and one container app. Optionally Azure Cache
for Redis and an Azure Files share.

---

### 8.2 What gets created, and why

Terraform in [`infra/`](infra/README.md) provisions all of this. The table is the reference for
*what* exists and *why*; the module is the authority on *how* it is configured.

| Resource | Purpose | Notes that matter |
|---|---|---|
| Resource group | container for everything | — |
| Container registry (Basic) | holds the application image | The only unavoidable charge — ACR has no free tier. |
| **Cosmos DB for MongoDB vCore** | the database | vCore, **not** the RU-based Mongo API: that is a compatibility layer over a different engine, and GrandNode relies on genuine MongoDB behaviour (GridFS, array update operators, aggregation). `compute_tier = "Free"` is confirmed available in South India. |
| Storage account (HNS on) | media + keys + lake | Hierarchical namespace makes directory rename/delete atomic, which lakehouse engines depend on. |
| Blob container `media` | product images | Public-read: images are served straight to browsers. Switches `IPictureService` to `AzurePictureService`. |
| Blob container `dpkeys` | data-protection key ring | **Must exist before the app starts.** Without it the key ring falls back to the container filesystem and every restart signs out every user. |
| Log Analytics + Application Insights | telemetry | Setting `ApplicationInsights__ConnectionString` activates the OpenTelemetry exporter `AddServiceDefaults()` already wires in. Do it on the first deployment, not after a performance problem. |
| Container Apps environment | hosts the app and jobs | — |
| Container app | the application | Port 8080, `/health/live` probes — see [section 8.14](#814-what-the-terraform-module-enforces). |
| Container Apps Jobs | scheduled tasks | Only when `min_replicas = 0` — see [section 8.5](#85-scheduled-tasks-with-scale-to-zero). |
| Azure Cache for Redis | cross-replica cache invalidation | Optional, **required** above one replica. |
| Azure Files shares | `App_Data`, media uploads | Optional; must be seeded before mounting. |

**Region.** Put the app, database and storage in the **same** region — cross-region database latency
dominates every request. Not `westeurope`: it is capacity-restricted for new subscriptions while
still appearing as a supported location, so a plan looks fine and the apply fails. Confirm what your
subscription actually offers:

```bash
az provider show -n Microsoft.App \
  --query "resourceTypes[?resourceType=='managedEnvironments'].locations | [0]" -o tsv | sort > /tmp/ca.txt
az provider show -n Microsoft.DocumentDB \
  --query "resourceTypes[?resourceType=='mongoClusters'].locations | [0]" -o tsv | sort > /tmp/mc.txt
comm -12 /tmp/ca.txt /tmp/mc.txt
```

That gives the intersection of regions offering both services — but not capacity restrictions, which
no API exposes.

---

### 8.3 Deploying

Terraform does not build images, so the sequence interleaves the two.

**1. Provision.** The container app is created before its image exists, so its first revision fails
to pull. That is expected.

```bash
cd infra/environments/prod
terraform init
terraform apply
```

**2. Build and push**, from the repository root:

```bash
az acr build -r $(terraform -chdir=infra/environments/prod output -raw container_registry_name) \
  -t grandnode:1 -f Dockerfile .

terraform -chdir=infra/environments/prod apply \
  -replace=module.grandnode.azurerm_container_app.this
```

`az acr build` takes roughly 8–9 minutes. ACR Tasks uses the classic Docker builder rather than
BuildKit, so it builds *every* stage in the Dockerfile — including the admin publish stage the
default target does not need.

**3. Install**, with a warm replica. `enable_installer = true` and `min_replicas = 1`; a `check`
block enforces that pairing, because the `/install` POST seeds the database inside a single HTTP
request and times out on a cold start.

```bash
terraform -chdir=infra/environments/prod output install_url
```

- The MongoDB fields on the form are **ignored** when `ConnectionStrings__Mongodb` is set.
- **Set Collation to `-None-`.** It does not default to it — `InstallController.PrepareModel`
  pre-selects the entry matching your UI language, and Cosmos vCore rejects every non-empty value
  with `Command create failed: Collation is currently not supported.`
- Choose the administrator email and password. `admin@yourstore.com` / `123456` in `README.md`
  belongs to the public demo site, not to your installation.

**4. Restart.** Not optional. `InstallController` calls `ResetCache()` on both the success and
failure paths, which latches `DatabaseIsInstalled()` to false for the life of the process; the app
then reports itself unhealthy and is removed from rotation.

```bash
az containerapp revision restart -g rg-grandnode-prod -n ca-grandnode-prod \
  --revision $(az containerapp revision list -g rg-grandnode-prod -n ca-grandnode-prod \
    --query "[?properties.active].name | [0]" -o tsv)
```

The same restart is the recovery after any *failed* install attempt, before retrying.

**5. Close the installer and return to steady state**: `enable_installer = false`, and either
`min_replicas = 1` or `min_replicas = 0` with `scheduled_task_jobs` defined.

---

### 8.4 Scaling out

Before raising `--max-replicas` above 1, three things must be in place, because the application's
cache is **per-instance** — `RedisMessageCacheManager` extends `MemoryCacheBase` and uses Redis
only as an invalidation message bus, not as a shared store.

**1. Redis, for cache invalidation across replicas.** Without it, a write on one replica leaves
the others serving stale data for up to `Cache__DefaultCacheTimeMinutes` (default 60). This is a
correctness requirement, not a tuning option.

```bash
az redis create -n grandnode-redis -g $RG -l $LOC --sku Basic --vm-size c0

export REDIS_CS="$(az redis show -n grandnode-redis -g $RG --query hostName -o tsv):6380,password=$(az redis list-keys -n grandnode-redis -g $RG --query primaryKey -o tsv),ssl=True,abortConnect=False"

az containerapp secret set -n $APPNAME -g $RG --secrets redis="$REDIS_CS"

az containerapp update -n $APPNAME -g $RG --set-env-vars \
  Redis__RedisPubSubEnabled=true \
  Redis__RedisPubSubConnectionString=secretref:redis \
  Redis__RedisPubSubChannel=grandnode-cache
```

**2. Blob-backed data protection and media** — already done in step 6. Verify `dpkeys` now
contains `keys.xml` before scaling.

**3. Then raise the replica ceiling:**

```bash
az containerapp update -n $APPNAME -g $RG \
  --min-replicas 1 --max-replicas 5 \
  --scale-rule-name http-rule --scale-rule-type http \
  --scale-rule-http-concurrency 50
```

Be aware of what horizontal scale does and does not buy you here: each replica keeps its own
cache, so N replicas cold-miss independently and MongoDB sees roughly N times the read load for
the same misses. Scale-out adds render and CPU capacity; it does not relieve the database. For a
read-heavy storefront, putting Azure Front Door or a CDN in front is usually the higher-leverage
change.

Scheduled tasks are safe on multiple replicas — `ScheduleTaskService.TryClaimTaskRun` does an
atomic compare-and-set so only one replica claims each run.

---

### 8.5 Scheduled tasks with scale-to-zero

GrandNode hosts its scheduled tasks as `BackgroundService` loops **inside the web host**
(`Grand.Web/Program.cs` calls `RegisterTasks`). They only advance while a web instance is running,
so with `min_replicas = 0` an idle deployment silently stops sending queued email, expiring unpaid
orders, ending auctions and draining the carrier event outbox — and nothing reports that it has
stopped.

Two ways to resolve it.

**Keep one replica warm.** `min_replicas = 1`. Simplest, always correct, and costs an always-on
container that exceeds the Container Apps free grant.

**Or run each task as a Container Apps Job.** The image accepts `--run-task <name>`, which executes
one task to completion and exits without starting Kestrel:

```bash
dotnet Grand.Web.dll --run-task "Send emails"
```

A job runs on its own cron, is billed only while executing, and preserves scale-to-zero for the web
app. In Terraform:

```hcl
min_replicas = 0

scheduled_task_jobs = {
  "Send emails"                      = { cron = "*/5 * * * *", timeout_seconds = 900 }
  "Cancel unpaid and pending orders" = { cron = "0 * * * *" }
  "End of the auctions"              = { cron = "*/15 * * * *" }
  "Delete guests"                    = { cron = "0 3 * * *" }
  "Update currency exchange rates"   = { cron = "0 4 * * *" }
  "Generate sitemap XML file"        = { cron = "0 2 * * 0", timeout_seconds = 1800 }
}
```

Four things that make this safe rather than merely convenient:

- **Keys must equal the `ScheduleTaskName` in the database**, which is also the DI registration key.
  A mismatch means the job runs and finds nothing to do.
- **Running alongside a web replica is safe.** `ScheduleTaskService.TryClaimTaskRun` does an atomic
  compare-and-set on `LastStartUtc`, so whichever process claims a run first executes it and the
  other stands down. No coordination was added for this; the lease already existed for
  multi-replica hosting.
- **The admin switch is honoured.** The runner reads the `ScheduleTask` row, so disabling a task in
  the admin panel stops the job too — not only the in-process loop.
- **Exit codes are meaningful.** Disabled or not-yet-seeded exits 0, because those are operator
  choices rather than faults. A missing DI registration or a thrown task exits 1, so a scheduler
  alerts instead of a job no-opping forever unnoticed.

Migrations stay with the storefront: every job sets `FeatureManagement__Grand.Module.Migration=false`,
because migrations have no cross-process lock and a job racing the web host at startup has nothing
to arbitrate.

---

### 8.6 Running and operating scheduled tasks

#### Every task has at least two switches

A task running is not enough for it to *do* anything. `ScheduleTask.Enabled` controls whether it
executes at all; several tasks then gate again on their own setting:

| Task | Second gate |
|---|---|
| `Update currency exchange rates` | `CurrencySettings.AutoUpdateEnabled` — seeded **false**. `Execute()` returns immediately when it is off. |
| `Send emails` | nothing to send unless the `QueuedEmail` collection has rows, **and** a real SMTP account is configured. The installer seeds `smtp.mail.com` with username `123` — a deliberate non-working stub. |
| `Apply carrier shipment events` | the `Shipping.CarrierTracking` plugin must be installed and a signing secret configured. |

A job that exits `Succeeded` having done nothing is therefore normal and expected. Check the logs,
not the exit status, to confirm work actually happened.

#### Enabling and disabling — admin UI

```
Admin → System → Schedule tasks          /admin/ScheduleTask/List
```

`System` is a top-level menu item; `Schedule tasks` is its fifth child, after `System information`,
`Queued emails`, `Contact Us form` and `Maintenance`. Requires the `ScheduleTasks` permission.

The edit screen exposes `Enabled`, `TimeInterval`, `StopOnError` and `StoreId`.

Two things about that screen in a jobs deployment:

- **`Enabled` governs both execution paths.** `ScheduleTaskRunner` reads the same row, so switching
  a task off here stops the Container Apps job as well as the in-process loop.
- **`TimeInterval` does not.** For tasks running as jobs the cadence is the cron in
  `scheduled_task_jobs`; changing the interval here affects only the in-process loop. To change a
  job's schedule, edit `infra/environments/prod/main.tf` and re-apply.

All tasks are seeded **disabled**, so a fresh installation runs no background work until an
administrator turns something on. Note this includes `Send emails`: until it is enabled, order
confirmations and password resets accumulate unsent in `Admin → System → Queued emails`.

#### Running one on demand

```bash
# list the jobs and their schedules
az containerapp job list -g rg-grandnode-prod \
  --query "[].{name:name,cron:properties.configuration.scheduleTriggerConfig.cronExpression}" -o table

# trigger one now
az containerapp job start -g rg-grandnode-prod -n caj-update-currency-exch-prod
```

The command returns an execution name such as `caj-update-currency-exch-prod-utkokb1`.

#### Checking the result

```bash
az containerapp job execution list -g rg-grandnode-prod -n caj-update-currency-exch-prod \
  --query "[].{name:name,status:properties.status,start:properties.startTime,end:properties.endTime}" -o table
```

`Succeeded` means the process exited 0. To see what it actually did, query the logs — the runner
logs one line on entry and one on completion:

```kusto
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(30m)
| where Log_s contains "ScheduleTaskRunner" or Log_s contains "Running task"
| project TimeGenerated, Log_s
| order by TimeGenerated asc
```

Expect a pair:

```
info: ScheduleTaskRunner[0]
      Running task 'Update currency exchange rates'
info: ScheduleTaskRunner[0]
      Task 'Update currency exchange rates' completed
```

`Task '<name>' is disabled - skipping` means the `Enabled` flag is off. That still exits 0 — a
disabled task is an operator's decision, not a failure, and a scheduler must not alert on it.

#### Running one locally

The same entry point works outside Azure, which is the quickest way to debug a task:

```bash
dotnet Grand.Web.dll --run-task "Send emails"
```

It builds the host, resolves the task, executes it, and exits without starting Kestrel. Exit `0`
succeeded or skipped, `1` failed.

---

### 8.7 Optional — split storefront / admin

Only worth doing for ingress isolation: Container Apps ingress restrictions apply to a whole app,
so a single container cannot restrict `/admin` alone without Front Door or Application Gateway in
front. Two apps give you per-app restrictions directly.

```bash
az acr build -r $ACR -t grandnode-web:1   -f Dockerfile --build-arg INCLUDE_ADMIN=false .
az acr build -r $ACR -t grandnode-admin:1 -f Dockerfile --target admin .
```

Deploy the storefront with `--ingress external` and the admin with `--ingress internal`, or with
an IP allowlist:

```bash
az containerapp ingress access-restriction set -n grandnode-admin -g $RG \
  --rule-name office --ip-address <your.office.ip>/32 --action Allow
```

Both apps need the identical values listed in
[section 5.3](#53-settings-that-must-be-identical-on-every-host-sharing-one-database), plus
`Extensions__InstalledPlugins` set to the same list on both — see
[section 7.2](#72-admin). Mount the same Azure Files share on both for the file manager:

```bash
az storage share-rm create --storage-account $STORAGE -g $RG -n media-share --quota 100

az containerapp env storage set -n $ENVNAME -g $RG \
  --storage-name mediashare --azure-file-account-name $STORAGE \
  --azure-file-account-key $(az storage account keys list -n $STORAGE -g $RG --query "[0].value" -o tsv) \
  --azure-file-share-name media-share --access-mode ReadWrite
```

Then reference that storage as a volume mounted at `/app/wwwroot/assets/images` in each app's
YAML (`az containerapp update --yaml`). Review the constraints in
[section 11](#11-known-constraints) first — in particular, the theme picker is empty in a
standalone admin container.

---

### 8.8 Alternative — App Service

**Containers:** deploy the same image to Linux App Service and add `WEBSITES_PORT=8080`. Set the
health check path to `/health/ready`. All the environment variables above apply unchanged.

**Code:** `azure-pipelines.yml` already publishes a zip artifact named `drop`. Add an
`AzureWebApp@1` task consuming `$(Build.ArtifactStagingDirectory)` and set the same values as App
Service application settings — the `__` nesting convention works there too.

App Service `/home` is persistent, so a single-instance deployment survives restarts without blob
storage. The blob settings remain correct for scale-out.

---

### 8.9 Custom domain and TLS

```bash
az containerapp hostname add -n $APPNAME -g $RG --hostname shop.example.com
az containerapp hostname bind -n $APPNAME -g $RG --hostname shop.example.com \
  --environment $ENVNAME --validation-method CNAME
```

Add the CNAME and TXT records your DNS provider requires first. Container Apps issues and renews
a managed certificate automatically. After binding, set the store URL in the admin panel under
Configuration → Stores so generated links and emails use the right host.

---

### 8.10 Updating a deployment

```bash
az acr build -r $ACR -t grandnode:2 -f Dockerfile .
az containerapp update -n $APPNAME -g $RG --image $ACR.azurecr.io/grandnode:2
```

Container Apps performs a rolling revision swap. Roll back by pointing at the previous tag, or
by reactivating the previous revision:

```bash
az containerapp revision list -n $APPNAME -g $RG -o table
az containerapp revision activate -n $APPNAME -g $RG --revision <previous-revision-name>
```

Database migrations run automatically at startup. They have no cross-process lock, so during a
rolling update two revisions can briefly run together — keep `Grand.Module.Migration` enabled on
exactly one app, and prefer single-revision mode for upgrades that carry migrations.

---

### 8.11 Troubleshooting

| Symptom | Likely cause |
|---|---|
| Container starts then exits immediately | Check `az containerapp logs show -n $APPNAME -g $RG --follow`. Most often an unreachable MongoDB, or a weak API secret key once `Grand.Module.Api` has been enabled. |
| Every URL redirects to `/install` | The database is empty or unreachable. Confirm `ConnectionStrings__Mongodb` and the database firewall. |
| Users signed out after each deploy | Data-protection keys not in blob storage. Verify `dpkeys` contains `keys.xml`. |
| Images upload then 404 | `Azure__AzureBlobStorageEndPoint` missing its trailing slash, or the `media` container is not public-read. |
| Stale prices or catalog on some requests | Multiple replicas without Redis pub/sub enabled. |
| Redirect loop, or links using `http://` | `Security__UseForwardedHeaders` not set to `true`. |
| Every page 302s to `/errorpage.htm`; logs show an antiforgery `SecurePolicy = Always` error | `Security__ForceUseHTTPS` not set — see [section 5.2](#52-behind-a-reverse-proxy-or-cloud-ingress). |
| Install fails with `Command create failed: Collation is currently not supported.` | Cosmos vCore rejects collation. Re-run the installer with Collation = `-None-`. |
| Every request times out after the installer ran (success **or** failure) | The readiness check is pinned unhealthy by `ResetCache()`. Restart the revision — see [section 8.3](#83-deploying). |
| First request after an idle period is very slow | `min_replicas = 0`. Expected; set to 1 to avoid it. |
| Queued email never sends, unpaid orders never expire, outbox never drains | `min_replicas = 0` with no `scheduled_task_jobs` — background work is hosted in the web process. See [section 8.5](#85-scheduled-tasks-with-scale-to-zero). |
| A scheduled-task job runs and does nothing | Its key does not match `ScheduleTaskName` in the database, or the task row is disabled in the admin panel. |
| App will not start after enabling volumes | The Azure Files share was mounted before being seeded, hiding `App_Data/appsettings.json`. |
| Admin theme picker is empty | Expected in a split admin container — see [section 11](#11-known-constraints). |

Useful commands:

```bash
az containerapp logs show -n $APPNAME -g $RG --follow
az containerapp revision list -n $APPNAME -g $RG -o table
curl -i https://<fqdn>/health/ready
```

---

### 8.12 Applying from a pipeline, and keeping the evidence

`terraform apply` writes a human-readable stream to the console. **Do not parse it, and do not keep
it as the record of what happened** — the format is not a stable interface and changes between
versions. Terraform has machine-readable paths for every verification purpose.

#### Plan to a file, and apply that file

The most important habit here is not about logging at all:

```bash
terraform plan -out=tfplan.bin
terraform apply tfplan.bin
```

Applying a saved plan closes the gap between what was reviewed and what runs — without it, state or
drift can change between the two commands and `apply` takes decisions nobody saw.

#### The full sequence deploying and saving structured data

```bash
cd infra/environments/prod

# 1. plan to a binary artifact, and keep a human-readable copy for the PR
terraform plan -out=tfplan.bin -input=false -no-color | tee plan.txt

# 2. the same plan as structured data
terraform show -json tfplan.bin > plan.json

# 3. policy gate: refuse a plan that destroys anything
jq -e '[.resource_changes[] | select(.change.actions[]=="delete")] | length == 0' plan.json

# 4. apply exactly what was planned, with machine-readable events
terraform apply -json -input=false tfplan.bin | tee apply.jsonl

# 5. what resulted
terraform output -json > outputs.json

# 6. validate the running deployment against the rules
../../scripts/check-deployment.sh -g rg-grandnode-prod -n ca-grandnode-prod
```

Step 3 is the point of the JSON: a gate on `plan.json` is a query, whereas grepping console output
for "0 destroyed" is guesswork against an unstable format.

#### Artifacts, and what each answers

| File | Answers | Keep? |
|---|---|---|
| `plan.json` | what was *intended* | yes — **sensitive** |
| `apply.jsonl` | what *happened*, event by event | yes — **sensitive** |
| `outputs.json` | what *resulted* | yes — **sensitive** |
| `plan.txt` | readable diff for a reviewer | yes, for the PR |
| `tfplan.bin` | the applied artifact | until applied — **sensitive** |

#### Treat plan files as secrets

A plan records the values it is about to set. In this module **16 of 21 resources carry sensitive
values**, including the Mongo connection string, the storage account key and the API signing keys.
A `.tfplan` and its JSON are as sensitive as the state file.

Never commit them, and never attach them to an unrestricted CI artifact. Encrypt, restrict and
expire them.

One thing that is *not* exposed: values generated during the apply. `random_string.mongo_password`
appears in the plan with only `length` and `special` — its `result` is unknown until apply, so the
generated passwords are not in `plan.json`.

#### After the fact, query state — not logs

Logs record what a run believed at the time. For what exists now:

```bash
terraform show -json > state.json      # every resource and attribute
terraform output -json                 # just the outputs
```

---

### 8.13 Validating a deployment against the rules

The `check` blocks in the next section only protect deployments made through Terraform. A
deployment made with the `az` CLI, from the portal, or by editing an existing app has no such
guard — and every one of these rules corresponds to a failure that is **silent in production**.

`infra/scripts/check-deployment.sh` applies the same rules to a live app. It is read-only.

```bash
./infra/scripts/check-deployment.sh -g rg-grandnode-prod -n ca-grandnode-prod
```

```
GrandNode deployment check: ca-grandnode-prod (rg: rg-grandnode-prod)
  minReplicas=0 maxReplicas=1 jobs=6 installer=false redis=unset

  PASS  installer_needs_a_warm_replica
  PASS  background_work_has_a_home
  PASS  redis_required_for_scale_out
  PASS  volumes_must_be_seeded_first (no mounts)
  PASS  force_https_behind_ingress
  PASS  readiness_probe_not_latching (/health/live)

All rules pass.
```

It checks the four `check` block rules plus the two settings that are easy to miss by hand —
`Security__ForceUseHTTPS`, and a readiness probe that must not point at `/health/ready`. Exit codes:
`0` all pass, `1` one or more violations, `2` could not inspect. Suitable for a post-deploy gate in
a pipeline.

Requires `az` (signed in) and `jq`.

---

### 8.14 What the Terraform module enforces

Five `check` blocks encode the failures this deployment actually hit, so they surface at plan time
instead of in production:

| Check | Fails when | Why it exists |
|---|---|---|
| `installer_needs_a_warm_replica` | `enable_installer = true` with `min_replicas = 0` | The `/install` POST seeds the database inside a single HTTP request; at scale-to-zero it cold-starts first and the browser times out before the app sees it. |
| `background_work_has_a_home` | `min_replicas = 0` and no `scheduled_task_jobs` | Scheduled tasks live in the web process — see [section 8.5](#85-scheduled-tasks-with-scale-to-zero). |
| `redis_required_for_scale_out` | `max_replicas > 1` with `enable_redis = false` | The cache is per-instance; replicas would serve stale prices and stock. Correctness, not performance. |
| `bundle_storage_needs_cors` | `enable_bundle_storage = true` with an empty `bundle_cors_origins` | Subresource Integrity forces a cross-origin fetch. Without an allowed origin the browser discards every bundle and the page renders with no JavaScript — silently. See [section 9.2](#92-provisioning-the-storage-container). |
| `volumes_must_be_seeded_first` | `enable_persistent_volumes = true` with `volumes_seeded = false` | An Azure Files mount replaces the directory the image ships. `/app/App_Data` carries `appsettings.json`, without which the host will not start. |

Two further settings the module applies that are easy to get wrong by hand:

- **Readiness probes point at `/health/live`, not `/health/ready`.** `/health/ready` runs
  `StartupHealthCheck`, whose `DatabaseIsInstalled()` value is latched to false by
  `InstallController`'s `ResetCache()` — on both the success *and* failure paths, with no way back
  without a new process. Wiring an orchestrator's readiness probe to it removes the replica from
  rotation permanently the moment anyone runs the installer.
- **A `startup_probe` gates liveness for up to five minutes.** The app loads every plugin and module
  assembly at boot; on a small CPU allocation a slow cold start otherwise reads as a liveness
  failure and the container is killed and retried forever.

---

## 9. Frontend assets — build, publish, and Subresource Integrity

The storefront's JavaScript and CSS live in `src/Web/Grand.Web/wwwroot/bundles/` and are baked
into the image. That means a one-line CSS change costs a full .NET image build, a registry push
and a new container revision, to move 830 kB of static files that the application never reads.

This section covers serving those files from Azure Storage instead, so a frontend release is an
upload plus a settings change. Subresource Integrity is what makes that safe: it is the reason the
storage account can be a dumb public bucket without becoming a way to inject script into the
storefront.

**All of it is opt-in.** With nothing configured the application serves `/bundles/*` from inside
the image exactly as before, and every page renders identically. Configuration lives in
`FrontendAssetSettings` — see [section 9.5](#95-wiring-it-up-in-the-admin-panel).

### 9.1 Building the bundles and the manifest

```bash
cd src/Web/Grand.Web/vueapp
npm ci
npm run build
```

`npm run build` is three steps: `vite build`, then `scripts/build-theme-css.mjs`, then
`build-asset-manifest.mjs`. Running `vite build` alone leaves the theme CSS and the manifest
stale.

Output, all in `src/Web/Grand.Web/wwwroot/bundles/`:

| File | Size | Referenced from |
|---|---|---|
| `app.runtime.bundle.js` | ~400 kB | `Views/Shared/Partials/Head.cshtml`, `Theme.Modern/.../Head.cshtml` |
| `libs.css` | ~319 kB | both `Head.cshtml` files |
| `style.min.css` | ~53 kB | `Head.cshtml` (LTR languages) |
| `style.rtl.min.css` | ~56 kB | `Head.cshtml` (RTL languages) |
| `asset-manifest.json` | ~700 B | **not** referenced by any view — see below |

These files are committed to the repository. That is deliberate and predates this feature: the
image build does not run npm, so an uncommitted bundle is simply not on the page. The
`Frontend CI` workflow rebuilds them on every PR touching `vueapp/`, `wwwroot/theme/css/` or
`wwwroot/bundles/` and fails if the committed output differs from the source — including the
manifest.

`asset-manifest.json` maps a logical asset name to a file and a `sha384` hash:

```json
{
  "version": 1,
  "assets": {
    "app.runtime.bundle.js": {
      "file": "app.runtime.bundle.js",
      "integrity": "sha384-zxiqcuS4jHHPcwvSprFpxb/BWJw0+CryiDS7TtFU5ao5F1YQMUqE7+pA7jxEOxZ0"
    }
  }
}
```

It deliberately carries no build timestamp. The file is committed and CI rebuilds it to check
the bundles still match their source, so a generation time would differ on every run and fail
that check permanently. A release is identified by the version prefix its files are uploaded
under.

The logical name is what a Razor view asks for, so the file on disk can be renamed or fingerprinted
later without touching a view.

### 9.2 Provisioning the storage container

Three variables on the Terraform module:

```hcl
module "grandnode" {
  # ...
  enable_bundle_storage = true
  bundle_cors_origins   = ["https://ca-grandnode-prod.<suffix>.azurecontainerapps.io"]

  # object IDs, not sign-in names
  bundle_publisher_object_ids = ["b8ec1442-7892-4e52-a29e-1b3c384d6d4c"]
}
```

`enable_bundle_storage` creates a `bundles` container on the existing storage account with
`container_access_type = "blob"` — anonymous read of blobs, no container listing. No new account,
no new cost line beyond the bytes stored.

`bundle_cors_origins` adds a CORS rule to the account's blob service allowing `GET` and `HEAD`
from the storefront's origin.

`bundle_publisher_object_ids` grants *Storage Blob Data Contributor* to each listed principal, so
releases can be published with `--auth-mode login` rather than the account key. Leave it empty and
[section 9.3](#93-publishing-a-release) fails with `You do not have the required permissions
needed to perform this operation` — an Owner or Contributor role on the subscription does **not**
convey data-plane access to blobs.

Look up an object ID rather than guessing it; the resource takes a GUID, not an email address:

```bash
az ad signed-in-user show --query id -o tsv     # yourself
az ad sp show --id <app-id> --query id -o tsv   # a CI service principal
```

Two things about this grant that are easy to trip over:

- **Applying it needs Owner or User Access Administrator.** Contributor cannot create role
  assignments, and the apply fails on `Microsoft.Authorization/roleAssignments/write`.
- **It is scoped to the storage account, not to the `bundles` container.** `upload-batch`
  enumerates the container before writing, which a container-scoped grant does not cover on a
  hierarchical-namespace account. That makes it a wider grant than bundles alone strictly needs —
  `media` is included — but still narrower and more auditable than the account key, which is a
  single shared secret covering every container with no attribution.

**CORS is not optional here, and getting it wrong fails silently.** A `<script>` carrying an
`integrity` attribute is fetched with `crossorigin="anonymous"`, which makes it a CORS request. A
response without a matching `Access-Control-Allow-Origin` is discarded by the browser: the page
renders with no JavaScript, nothing appears in the application logs, and the storage access log
records a successful `200`. A `check` block refuses the plan rather than letting that reach
production:

```
enable_bundle_storage = true requires bundle_cors_origins. Subresource Integrity forces
a CORS fetch; without an allowed origin the browser silently discards the bundles.
```

Take the origin from the `application_url` output, with no trailing slash and no path. If a custom
domain is bound ([section 8.9](#89-custom-domain-and-tls)), list both — the browser sends whichever
origin the page was served from.

### 9.3 Publishing a release

Upload into a **version prefix**. The prefix is what makes a release atomic: new files land beside
the old ones and nothing switches until the setting is updated, so a rollback is a settings change
rather than a re-upload.

```bash
ACCOUNT=$(terraform -chdir=infra/environments/prod output -raw storage_account_name)
VERSION=v1

# Stage everything except the manifest. The exclusion is the point, not a tidiness
# measure - see section 9.7.
STAGE=$(mktemp -d)
cp -r src/Web/Grand.Web/wwwroot/bundles/. "$STAGE/"
rm -f "$STAGE/asset-manifest.json"

az storage blob upload-batch \
  --account-name "$ACCOUNT" \
  --destination bundles \
  --destination-path "$VERSION" \
  --source "$STAGE" \
  --content-cache 'public, max-age=31536000, immutable' \
  --auth-mode login \
  --overwrite false

rm -rf "$STAGE"
```

Notes on each part that matters:

- **`asset-manifest.json` is removed from the staging copy before the upload.** The hashes must not
  travel with the files they describe. See [section 9.7](#97-what-sri-actually-protects). Do not
  reach for `--pattern` to do this: `--pattern` is Python `fnmatch`, which supports only `*`, `?`,
  `[seq]` and `[!seq]` — no brace expansion — so `'*.{js,css}'` matches nothing and uploads an
  empty release without failing.
- **Staging a copy also carries `fonts/` along**, which `libs.css` references by relative path and
  which an extension filter would drop.
- **`--content-cache ... immutable`** is safe only because of the version prefix. A given URL under
  `v1/` never changes content; the next release is `v2/`. Without the prefix this caches a stale
  bundle in every visitor's browser for a year.
- **`--overwrite false`** turns an accidental re-upload into a `ResourceExistsError` instead of
  silently changing a file that browsers have been told is immutable — and that the stored hashes
  still describe.
- **`--auth-mode login`** uses your Entra identity, and needs the *Storage Blob Data Contributor*
  role granted by `bundle_publisher_object_ids` in [section 9.2](#92-provisioning-the-storage-container).
  *Owner* alone does not convey data-plane access. As a fallback, `--auth-mode key` works whenever
  the account still allows shared-key access, but it uses one secret that covers every container on
  the account and leaves no record of who published.

Confirm the content types survived the upload — a `.js` blob served as
`application/octet-stream` is refused by some browsers:

```bash
az storage blob show --account-name "$ACCOUNT" -c bundles \
  -n "$VERSION/app.runtime.bundle.js" --auth-mode login \
  --query "properties.contentSettings.contentType" -o tsv     # expect application/javascript
```

### 9.4 The base URL

```
https://<storage-account>.blob.core.windows.net/bundles/<version>/
```

The trailing slash is required and is enforced by the validator. Behind a CDN or Front Door,
use that hostname instead; nothing else changes.

### 9.5 Wiring it up in the admin panel

**Settings → Frontend asset settings** (`/admin/Setting/FrontendAsset`).

| Field | Value |
|---|---|
| Base URL | the URL from 9.4, absolute, with trailing slash. Empty = serve from the image. |
| Manifest | the entire contents of `asset-manifest.json`, pasted |
| Use Subresource Integrity | on |

This screen has no store scope selector, and that is deliberate — see
[section 9.11](#911-limitations). The values apply to every store.

Both fields are validated on save: the base URL must be absolute `http`/`https` with a trailing
slash, and the manifest must be JSON containing an `assets` object. Both failures are otherwise
invisible — a bad base URL 404s every bundle, and a manifest without `assets` parses cleanly and
resolves nothing, which looks exactly like not having configured the feature at all.

Below the form, a **Resolved assets** table shows the URL and hash the application will actually
emit for each of the four bundles. It reflects the *running* configuration, not what is in the
form, which makes it the check that the restart below has taken effect.

**Restart the application after saving.** `IFrontendAssetResolver` is a singleton that parses the
manifest once, so a running process keeps serving the previous release until it is recycled. The
screen says so on save. On Container Apps:

```bash
az containerapp revision restart -g <rg> -n <app> \
  --revision "$(az containerapp revision list -g <rg> -n <app> \
    --query "[?properties.active].name | [0]" -o tsv)"
```

Then reload the settings page and confirm the Resolved assets table shows the new URLs.

### 9.6 How a page picks the bundle up

```
Head.cshtml                 <script asp-location="Head" asp-src="/bundles/app.runtime.bundle.js">
  ↓
ScriptTagHelper             strips "/bundles/", asks the resolver for "app.runtime.bundle.js"
LinkTagHelper               (same, for stylesheets)
  ↓
IFrontendAssetResolver      manifest lookup → { Url, Integrity }, or null
  ↓
rendered markup             <script src="https://…/v1/app.runtime.bundle.js"
                                    integrity="sha384-…" crossorigin="anonymous" defer>
```

Three behaviours worth knowing:

- **A `null` from the resolver is the normal case, not an error.** Anything not in the manifest —
  every plugin script, every theme asset, all of `/assets/*` — keeps the path written in the view.
  That is what keeps the default deployment working with no configuration at all.
- **`asp-append-version` is suppressed for a manifest-resolved asset.** The version prefix already
  changes per release, and hashing a file that is no longer served locally would produce a wrong
  answer. It still applies to everything else.
- **`src=` and `asp-src=` are not interchangeable.** Only `asp-src` binds the tag helper; a plain
  `src` renders untouched and never reaches the resolver. Both `Head.cshtml` files used plain `src`
  on the script tag and were changed as part of this work. Any new view that wants a managed bundle
  must use `asp-src`.

### 9.7 What SRI actually protects

The browser is told the hash a file must have. A file that does not match is not executed —
regardless of who served it.

That only means something because **the hash and the file reach the browser by different paths**:

| Travels via | Carries |
|---|---|
| Azure Storage → browser | the bundle |
| MongoDB → application → HTML | the hash |

Whoever holds the storage key can replace `app.runtime.bundle.js`. They cannot make a browser run
it, because the hash in the page still describes the old file and the script is blocked. To
actually inject script, an attacker needs the storage account *and* database write access — which
is a materially higher bar than a leaked SAS token or a misconfigured container.

Publish both from the same place and this collapses to nothing: change the file, change the hash
alongside it, and the browser is satisfied. **That is why `asset-manifest.json` is excluded from
the upload in 9.3 and pasted into settings instead.** Uploading it next to the bundles would leave
the mechanism in place and the protection gone, and nothing would look wrong.

What it does not do: SRI is integrity, not confidentiality or availability. It does not stop a
deleted bundle, a hostile CDN serving nothing, or anything at all once an attacker has database
write access.

### 9.8 Rolling back

Point the base URL at the previous prefix, restore that release's manifest, restart. The old files
were never deleted.

To abandon external serving entirely, clear **Base URL** and restart: the application falls back to
the bundles inside the image, which have been there the whole time.

Both are settings changes. Neither needs a rebuild, a redeploy, or a Terraform apply.

### 9.9 When it goes wrong

Every failure here is silent in the application logs, because none of it involves the application
at request time. Read the browser console first.

| Symptom | Cause |
|---|---|
| Page renders unstyled with no interactivity; console shows a CORS error | Origin missing from `bundle_cors_origins`, or the app is reached on a hostname that is not in the list |
| Console: "Failed to find a valid digest ... integrity attribute" | The uploaded file does not match the pasted manifest — usually a manifest from a different build |
| 404 on every bundle | Base URL missing its trailing slash, or the version prefix does not exist |
| Settings saved, page unchanged | The process was not restarted; the singleton still holds the previous manifest |
| Resolved assets table is empty | The manifest has no `assets` object, or it failed to parse — the resolver logs a parse failure once and falls back to local bundles |
| Some bundles switch, others do not | Those views use plain `src=` rather than `asp-src=` |

### 9.10 Restricting where scripts may come from

Subresource Integrity stops a *modified* bundle from executing. It says nothing about a script
tag that was never yours — one injected through a stored-XSS hole, pointing at an attacker's
host. That is what `script-src` is for, and the two are complementary.

```hcl
module "grandnode" {
  # ...
  enable_default_security_headers = true
  script_src_allowed_hosts        = ["https://www.googletagmanager.com"]
}
```

The bundle origin is added automatically when `enable_bundle_storage = true`, because forgetting
it is the obvious way to take the storefront down with this setting. List everything else the
storefront legitimately loads script from: analytics, payment provider SDKs, chat widgets, and
anything a plugin injects a `<script src>` for.

**Be honest about what this achieves.** The policy still carries `unsafe-inline` and
`unsafe-eval`, and neither can be removed here:

- 64 storefront views render inline `<script>` blocks.
- Vue ships the **runtime template compiler** — `vite.config.js` aliases `vue` to
  `vue.esm-bundler.js` — because the templates *are* the Razor markup, parsed out of the DOM and
  compiled with `new Function()` on every page. `unsafe-eval` is load-bearing; removing it
  renders a blank storefront.

So this narrows *where a file may come from*, not *what may run*. Against an attacker who can
inject `<script src="https://evil.example/x.js">` it is effective; against one who can inject an
inline `<script>` it is not. That is still a material improvement over the shipped default of
`script-src *`, which permits both.

Turning it on also enables the other default headers — HSTS, `X-Frame-Options: Deny`,
`X-Content-Type-Options: nosniff`, a referrer policy, and a Permissions-Policy that already grants
`camera`, `microphone`, `geolocation` and `payment` to `self`.

**Verify in the browser, not the logs.** A CSP violation is reported to the console and nowhere
else — the server never learns of it:

```bash
curl -sI https://<storefront>/ | grep -i content-security-policy
```

Then load the storefront with the console open and click through a product page, the cart and
checkout. A blocked resource names the directive that refused it, which tells you exactly what to
add to `script_src_allowed_hosts`.

### 9.11 Limitations

**The configuration is global, not per-store.** `IFrontendAssetResolver` is a singleton built on
first use, taking `FrontendAssetSettings` through its constructor. The settings registration reads
the store id from `IContextAccessor`, which is an `AsyncLocal` — so the scope the resolver captures
is whichever store served the first request after a restart, and no store at all if anything
resolves it outside a request. That is not a scope an administrator can aim at, so the screen has
no store selector and writes to the global scope, which `SettingService.LoadSetting` falls back to
for any store without an override of its own. A multi-store installation serves the same bundles
to every store.

**The manifest is duplicated by hand.** It is generated by the frontend build and pasted into
settings; nothing verifies the two agree. A stale paste is caught by the browser (blocked scripts)
rather than at save time.

**No CDN in front by default.** The blob endpoint serves the bundles directly. That is adequate for
a single-region deployment; a global storefront should put Front Door or a CDN profile in front of
the container and set the base URL to that hostname.

---

## 10. Verification

Two health endpoints are exposed by every image:

| Endpoint | Checks |
|---|---|
| `/health/live` | the process can respond; never touches an external dependency |
| `/health/ready` | the application finished starting and is configured |

Neither probes MongoDB or Redis — readiness covers the application process only.

```bash
curl -i http://localhost:8080/health/live     # expect 200 Healthy
curl -i http://localhost:8080/health/ready    # expect 200 Healthy
```

For a split deployment, confirm the separation:

```bash
curl -o /dev/null -w "%{http_code}\n" http://localhost:8080/            # storefront -> 200
curl -o /dev/null -w "%{http_code}\n" http://localhost:8080/admin/login # storefront -> 404
curl -o /dev/null -w "%{http_code}\n" http://localhost:8081/admin/login # admin      -> 200
```

Inspect image contents:

```bash
docker run --rm --entrypoint sh grandnode-web -c "ls /app/Grand.Web.Admin.dll"   # expect: not found
docker run --rm --entrypoint sh grandnode-admin -c "ls /app/Plugins /app/Modules"
```

If a container starts and immediately exits, check `docker logs <name>` first. The most common
causes are a weak `BackendAPI__SecretKey` / `FrontendAPI__SecretKey` when the API module has been
enabled, and an unreachable MongoDB.

---

## 11. Known constraints

> Sections 1-7 and 9 were exercised against local Docker containers and a seeded MongoDB.
> Section 8 was executed end to end against a live Azure subscription in South India on
> 2026-09-04 — resource group through to a serving storefront and admin panel. The findings
> below come from that deployment, not from reading the code alone.


**The theme picker is empty in a standalone admin container.** Storefront themes compile against
the `Grand.Web` assembly itself with `ExcludeAssets=all`, resolving it from the host at runtime.
An admin-only process does not have that assembly, and `PluginManager.Load` fails hard on a plugin
it cannot load — so shipping a theme in the admin image would stop the host from starting. Theme
plugins are therefore excluded from the admin image, and admin Settings → General/Common, which
injects `IEnumerable<IThemeView>`, has nothing to list. The storefront continues to use whichever
theme is stored in the database. To change themes, run the all-in-one image temporarily.

**Migrations have no distributed lock.** `MigrationProcess.RunMigrationProcess` reads the applied
set once and iterates, so two hosts starting simultaneously can run the same migration twice. Only
one host may have `Grand.Module.Migration` enabled. This applies to multiple storefront replicas
too, not only to a split deployment.

**Plugin install state is a local file.** Installing a plugin from one host writes only that
host's `InstalledPlugins.cfg`. Use `Extensions__InstalledPlugins` to keep hosts in agreement.

**The file manager always writes local disk.** `IMediaFileStore` is a `FileSystemStore` over
`WebRootPath` regardless of blob-storage configuration, so elFinder uploads need a shared volume.

**One database, no isolation.** Splitting the admin is a deployment-topology change for ingress
control and blast radius. It is not a fault-isolation boundary — both hosts share one MongoDB.

**A completed or failed installation leaves the process unable to serve traffic.**
`InstallController` calls `DataSettingsManager.Instance.ResetCache()` on both paths, and
`ResetCache()` sets `_databaseIsInstalled = false` with no way back short of a new process
(`DataSettingsManager.cs`). `StartupHealthCheck` consequently reports
`Database connection is not configured.`, readiness fails, and the orchestrator stops routing to
the replica. `StartupHealthCheck`'s own remarks acknowledge this and note that the installer is
expected to ask for a restart. Restart the revision after installing, and after any failed
attempt.

**Cosmos DB for MongoDB vCore does not support collation**, so it must be installed with
Collation = `-None-`. Before the fix in
`src/Modules/Grand.Module.Installer/Services/InstallationService.cs`, an empty collation caused
`CreateTables` to return early, which also skipped `CreateIndexes` — an installation that appeared
to succeed but had none of its ~170 indexes, giving collection scans on every query and
unenforced unique constraints. If you deploy an image built before that change, do not use
`-None-`.

**Locale-aware sorting is lost without collation.** Database-level string comparison becomes
ordinal, so sort order for accented characters differs from native expectations. Account identity
is unaffected — emails and usernames are lowercased in application code
(`CustomerService.cs:236, 349, 401, 534`) rather than relying on collation. If locale-correct
sorting matters for your market, that is an argument for MongoDB Atlas over Cosmos vCore.
