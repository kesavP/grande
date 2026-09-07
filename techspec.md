# GrandNode2 — Docker & Azure Deployment Technical Specification

This document covers building the Docker images, generating the API secret keys, running
the application in Docker, and deploying it to Azure.

- [1. What is being deployed](#1-what-is-being-deployed)
- [2. Prerequisites](#2-prerequisites)
- [3. Building the images](#3-building-the-images)
- [4. Generating the API secret keys](#4-generating-the-api-secret-keys)
- [5. Configuration reference](#5-configuration-reference)
- [6. Running in Docker — single container](#6-running-in-docker--single-container)
- [7. Running in Docker — split storefront / admin](#7-running-in-docker--split-storefront--admin)
- [8. Deploying to Azure](#8-deploying-to-azure)
  - [8.1 Prerequisites](#81-prerequisites)
  - [8.2 Step 1 — Variables and resource group](#82-step-1--variables-and-resource-group)
  - [8.3 Step 2 — Container registry and image](#83-step-2--container-registry-and-image)
  - [8.4 Step 3 — MongoDB](#84-step-3--mongodb)
  - [8.5 Step 4 — Storage for media and keys](#85-step-4--storage-for-media-and-keys)
  - [8.6 Step 5 — Application Insights](#86-step-5--application-insights)
  - [8.7 Step 6 — Container Apps environment and the app](#87-step-6--container-apps-environment-and-the-app)
  - [8.8 Step 7 — First-run installation](#88-step-7--first-run-installation)
  - [8.9 Scaling out](#89-scaling-out)
  - [8.10 Optional — split storefront / admin](#810-optional--split-storefront--admin)
  - [8.11 Alternative — App Service](#811-alternative--app-service)
  - [8.12 Custom domain and TLS](#812-custom-domain-and-tls)
  - [8.13 Updating a deployment](#813-updating-a-deployment)
  - [8.14 Troubleshooting](#814-troubleshooting)
- [9. Verification](#9-verification)
- [10. Known constraints](#10-known-constraints)

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
and [section 10](#10-known-constraints) for the trade-offs.

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
the recommended target; [section 8.11](#811-alternative--app-service) covers App Service.

> **Two paths to the same result.** This section is the imperative `az` CLI walkthrough — useful
> for understanding exactly what gets created, and for a one-off environment. For anything
> repeatable, use the Terraform module in [`infra/`](infra/README.md), which provisions the same
> resources with the same settings. The explanations below apply to both; the module's README
> covers only what is specific to running it.

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
| Topology | single container, or split storefront/admin | Start single. Split only for ingress isolation — see [section 8.10](#810-optional-split-storefront--admin). |
| Scale | 1 replica, or many | Start at 1. Multi-replica requires Redis and blob storage first — see [section 8.9](#89-scaling-out). |
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

### 8.2 Step 1 — Variables and resource group

Set these once; every later command reuses them.

```bash
export RG=grandnode-rg
export LOC=southindia
export ACR=grandnodeacr$RANDOM          # must be globally unique, lowercase alphanumeric
export ENVNAME=grandnode-env
export APPNAME=grandnode
export STORAGE=grandnodestor$RANDOM     # must be globally unique, lowercase alphanumeric
export MONGO_DB_NAME=grandnode

az login
az account set --subscription "<your subscription name or id>"
az group create -n $RG -l $LOC
```

---

### 8.3 Step 2 — Container registry and image

```bash
az acr create -n $ACR -g $RG --sku Basic --admin-enabled true

# builds from the repository root, inside Azure
az acr build -r $ACR -t grandnode:1 -f Dockerfile .
```

Run this from the repository root — the build context must include `src/` and
`Directory.Packages.props`. The first build takes several minutes because it compiles every
module and plugin.

Tag each deployment with an incrementing version (`grandnode:2`, `:3`) rather than reusing
`latest`, so a rollback is just a redeploy of the previous tag.

---

### 8.4 Step 3 — MongoDB

**Option A — Cosmos DB for MongoDB vCore**

```bash
az cosmosdb mongocluster create \
  --resource-group $RG --cluster-name grandnode-mongo --location $LOC \
  --administrator-login grandnodeadmin \
  --administrator-login-password "<a strong password>" \
  --server-version 7.0 \
  --shard-node-tier M30 --shard-node-ha false \
  --shard-node-disk-size-gb 128 --shard-node-count 1
```

Then allow the Container Apps environment to reach it. For a first deployment you can permit
Azure services; tighten this to a private endpoint before going live:

```bash
az cosmosdb mongocluster firewall rule create \
  --resource-group $RG --cluster-name grandnode-mongo \
  --rule-name allow-azure --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
```

Retrieve the connection string from the portal (Connection strings blade) and substitute your
password. It looks like:

```
mongodb+srv://grandnodeadmin:<password>@grandnode-mongo.global.mongocluster.cosmos.azure.com/grandnode?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false
```

Append the database name (`/grandnode`) to the path — the application does not create it from a
separate setting.

**Option B — MongoDB Atlas**

Create an M10 or larger cluster in the same Azure region, add a database user, and allow access
from the Container Apps environment's outbound IPs (or use VNet peering). Connection string:

```
mongodb+srv://user:password@cluster.mongodb.net/grandnode?retryWrites=true&w=majority
```

Either way, verify the string ends with the database name and keep it for step 6.

---

### 8.5 Step 4 — Storage for media and keys

Two blob containers are needed for anything beyond a single throwaway instance. **Create them
now** — the data-protection container must exist before the app starts, or key persistence fails.

```bash
az storage account create -n $STORAGE -g $RG -l $LOC --sku Standard_LRS --kind StorageV2

export STORAGE_CS=$(az storage account show-connection-string \
  -n $STORAGE -g $RG --query connectionString -o tsv)

az storage container create -n media      --connection-string "$STORAGE_CS" --public-access blob
az storage container create -n dpkeys     --connection-string "$STORAGE_CS"
```

`media` is public-read because product images are served directly to browsers. `dpkeys` must
stay private.

Note the endpoint for later — it needs a **trailing slash**, because the application
concatenates it with the container name:

```bash
export BLOB_ENDPOINT="https://$STORAGE.blob.core.windows.net/"
```

---

### 8.6 Step 5 — Application Insights

```bash
az monitor log-analytics workspace create -g $RG -n grandnode-logs -l $LOC

export APPI_CS=$(az monitor app-insights component create \
  --app grandnode-insights -g $RG -l $LOC \
  --workspace grandnode-logs \
  --query connectionString -o tsv)
```

Setting this connection string activates the OpenTelemetry exporter that `AddServiceDefaults()`
already wires into every host — request durations, dependency calls and failure rates, with no
code change. Do this on the first deployment, not after you have a performance problem.

---

### 8.7 Step 6 — Container Apps environment and the app

```bash
az containerapp env create -n $ENVNAME -g $RG -l $LOC \
  --logs-workspace-id $(az monitor log-analytics workspace show -g $RG -n grandnode-logs --query customerId -o tsv) \
  --logs-workspace-key $(az monitor log-analytics workspace get-shared-keys -g $RG -n grandnode-logs --query primarySharedKey -o tsv)

az containerapp create -n $APPNAME -g $RG \
  --environment $ENVNAME \
  --image $ACR.azurecr.io/grandnode:1 \
  --registry-server $ACR.azurecr.io \
  --target-port 8080 --ingress external \
  --min-replicas 1 --max-replicas 1 \
  --cpu 1 --memory 2Gi
```

`--target-port 8080` matches the image's `EXPOSE`. Keep `--max-replicas 1` until
[section 8.9](#89-scaling-out) is done — additional replicas without Redis serve stale data.

**Secrets and configuration**

```bash
az containerapp secret set -n $APPNAME -g $RG --secrets \
  mongo="<the connection string from step 3>" \
  storage="$STORAGE_CS" \
  hashkey="$(openssl rand -hex 24)" \
  backendkey="$(openssl rand -hex 24)" \
  frontendkey="$(openssl rand -hex 24)" \
  appinsights="$APPI_CS"

az containerapp update -n $APPNAME -g $RG --set-env-vars \
  ConnectionStrings__Mongodb=secretref:mongo \
  ApplicationInsights__ConnectionString=secretref:appinsights \
  Security__PasswordHashKey=secretref:hashkey \
  BackendAPI__SecretKey=secretref:backendkey \
  FrontendAPI__SecretKey=secretref:frontendkey \
  Security__UseForwardedHeaders=true \
  Security__CookieSecurePolicyAlways=true \
  Security__UseHsts=true \
  Application__DisplayFullErrorStack=false \
  Azure__AzureBlobStorageConnectionString=secretref:storage \
  Azure__AzureBlobStorageContainerName=media \
  Azure__AzureBlobStorageEndPoint="$BLOB_ENDPOINT" \
  Azure__PersistKeysToAzureBlobStorage=true \
  Azure__PersistKeysAzureBlobStorageConnectionString=secretref:storage \
  Azure__DataProtectionContainerName=dpkeys \
  Azure__DataProtectionBlobName=keys.xml
```

Why each of the non-obvious ones:

- `Security__UseForwardedHeaders=true` — Container Apps ingress terminates TLS. Without this the
  app sees plain HTTP and generates wrong redirects and cookie flags.
- `Security__PasswordHashKey` — the PBKDF2 pepper. **Set it before the first customer registers.**
  Changing it later invalidates every existing password hash.
- `Azure__PersistKeysToAzureBlobStorage` — without it, the data-protection key ring lives on the
  container filesystem and every restart signs out every user.
- `Azure__AzureBlobStorageConnectionString` — switches `IPictureService` to `AzurePictureService`,
  so product images survive a restart.
- The two API keys are only enforced when `FeatureManagement__Grand.Module.Api=true`, but setting
  them now means enabling the API later cannot fail startup.

Get the URL:

```bash
az containerapp show -n $APPNAME -g $RG --query properties.configuration.ingress.fqdn -o tsv
```

---

### 8.8 Step 7 — First-run installation

Browse to `https://<fqdn>`. The application redirects to `/install`.

- The MongoDB connection fields on that form are **ignored** when `ConnectionStrings__Mongodb`
  is set — `InstallController` prefers the configured value. Fill them if client-side validation
  insists, but the configured string is what gets used.
- Enter the **administrator email and password**. These become your admin credentials; the form
  pre-fills `admin@yourstore.com` but the password is yours to choose. The
  `admin@yourstore.com` / `123456` pair in `README.md` belongs to the public demo site only.
- Choose whether to load sample data. Skip it for production.

- **Collation: choose `-None-` when the database is Cosmos DB for MongoDB vCore.** The dropdown
  does *not* default to it — `InstallController.PrepareModel` pre-selects the entry matching the
  UI language, so it arrives showing e.g. "English" and looks already answered. vCore rejects
  collation on collection creation and the install fails with
  `Command create failed: Collation is currently not supported.` MongoDB Atlas accepts any value.

Installation seeds roughly 110 collections plus about 170 indexes, and takes a minute or two.

**Restart the app once installation finishes.** This is not optional. `InstallController` calls
`DataSettingsManager.Instance.ResetCache()` on both the success and the failure path, and
`ResetCache()` can only force `_databaseIsInstalled` to *false* — never back to true without a new
process. `StartupHealthCheck` then reports `Database connection is not configured.` for the rest of
the process lifetime, Container Apps stops routing traffic to the replica, and **every request
times out**. It looks like a hang or a cold start; it is neither, and it does not self-recover.

```bash
az containerapp revision restart -n $APPNAME -g $RG \
  --revision $(az containerapp revision list -n $APPNAME -g $RG \
    --query "[?properties.active].name | [0]" -o tsv)
```

The same restart is the recovery after any *failed* install attempt, before retrying.

**Then harden it:**

```bash
az containerapp update -n $APPNAME -g $RG --set-env-vars \
  FeatureManagement__Grand.Module.Installer=false
```

The installer intercepts every path when it thinks the database is empty; leaving it enabled in
production is an unnecessary exposure.

The storefront is now at `https://<fqdn>/` and the admin panel at `https://<fqdn>/admin`.

---

### 8.9 Scaling out

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

### 8.10 Optional — split storefront / admin

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
[section 10](#10-known-constraints) first — in particular, the theme picker is empty in a
standalone admin container.

---

### 8.11 Alternative — App Service

**Containers:** deploy the same image to Linux App Service and add `WEBSITES_PORT=8080`. Set the
health check path to `/health/ready`. All the environment variables above apply unchanged.

**Code:** `azure-pipelines.yml` already publishes a zip artifact named `drop`. Add an
`AzureWebApp@1` task consuming `$(Build.ArtifactStagingDirectory)` and set the same values as App
Service application settings — the `__` nesting convention works there too.

App Service `/home` is persistent, so a single-instance deployment survives restarts without blob
storage. The blob settings remain correct for scale-out.

---

### 8.12 Custom domain and TLS

```bash
az containerapp hostname add -n $APPNAME -g $RG --hostname shop.example.com
az containerapp hostname bind -n $APPNAME -g $RG --hostname shop.example.com \
  --environment $ENVNAME --validation-method CNAME
```

Add the CNAME and TXT records your DNS provider requires first. Container Apps issues and renews
a managed certificate automatically. After binding, set the store URL in the admin panel under
Configuration → Stores so generated links and emails use the right host.

---

### 8.13 Updating a deployment

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

### 8.14 Troubleshooting

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
| Every request times out after the installer ran (success **or** failure) | The readiness check is pinned unhealthy by `ResetCache()`. Restart the revision — see [section 8.8](#88-step-7--first-run-installation). |
| First request after an idle period is very slow | `min_replicas = 0`. Expected; set to 1 to avoid it. |
| Admin theme picker is empty | Expected in a split admin container — see [section 10](#10-known-constraints). |

Useful commands:

```bash
az containerapp logs show -n $APPNAME -g $RG --follow
az containerapp revision list -n $APPNAME -g $RG -o table
curl -i https://<fqdn>/health/ready
```

---

## 9. Verification

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

## 10. Known constraints

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
