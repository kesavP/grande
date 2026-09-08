#!/usr/bin/env bash
#
# Validates a live GrandNode Container App against the rules the Terraform module
# enforces at plan time.
#
# The check blocks in modules/grandnode only protect deployments made through
# Terraform. A deployment made with the az CLI, from the portal, or by editing an
# existing app has no such guard - and every rule below corresponds to a failure
# that is silent in production: background work that stops, replicas serving stale
# prices, an app that cannot boot, a form that times out.
#
# Read-only: it inspects, it never changes anything.
#
# Usage:
#   check-deployment.sh -g <resource-group> -n <container-app> [-e <environment>]
#
# Exit codes: 0 all rules pass, 1 one or more violations, 2 could not inspect.

set -uo pipefail

RG=""; APP=""; ENV_NAME=""
while getopts "g:n:e:h" opt; do
    case $opt in
        g) RG="$OPTARG" ;;
        n) APP="$OPTARG" ;;
        e) ENV_NAME="$OPTARG" ;;
        h) sed -n '2,20p' "$0"; exit 0 ;;
        *) exit 2 ;;
    esac
done

if [ -z "$RG" ] || [ -z "$APP" ]; then
    echo "usage: $(basename "$0") -g <resource-group> -n <container-app> [-e <environment>]" >&2
    exit 2
fi

command -v az >/dev/null || { echo "az CLI not found" >&2; exit 2; }
command -v jq >/dev/null || { echo "jq not found" >&2; exit 2; }

APP_JSON=$(az containerapp show -g "$RG" -n "$APP" -o json 2>/dev/null)
if [ -z "$APP_JSON" ]; then
    echo "Could not read container app '$APP' in resource group '$RG'." >&2
    echo "Check the names, and that you are signed in to the right subscription." >&2
    exit 2
fi

failures=0
pass() { printf '  \033[32mPASS\033[0m  %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; printf '        %s\n' "$2"; failures=$((failures+1)); }
warn() { printf '  \033[33mWARN\033[0m  %s\n' "$1"; printf '        %s\n' "$2"; }

env_value() { jq -r --arg n "$1" '.properties.template.containers[0].env[]? | select(.name==$n) | (.value // "<secret>")' <<<"$APP_JSON"; }

MIN=$(jq -r '.properties.template.scale.minReplicas // 0' <<<"$APP_JSON")
MAX=$(jq -r '.properties.template.scale.maxReplicas // 1' <<<"$APP_JSON")
INSTALLER=$(env_value "FeatureManagement__Grand.Module.Installer")
REDIS=$(env_value "Redis__RedisPubSubEnabled")
FORCE_HTTPS=$(env_value "Security__ForceUseHTTPS")
# Probes come back as one array discriminated by .type - NOT as separate
# readinessProbes[]/livenessProbes[] arrays. Reading the wrong path returns null,
# which made this rule report "none" and silently pass whatever was configured.
READINESS=$(jq -r '[.properties.template.containers[0].probes[]? | select(.type=="Readiness")][0].httpGet.path // "none"' <<<"$APP_JSON")
LIVENESS=$(jq -r '[.properties.template.containers[0].probes[]? | select(.type=="Liveness")][0].httpGet.path // "none"' <<<"$APP_JSON")
MOUNTS=$(jq -r '[.properties.template.containers[0].volumeMounts[]?] | length' <<<"$APP_JSON")

ENV_ID=$(jq -r '.properties.environmentId // .properties.managedEnvironmentId // ""' <<<"$APP_JSON")
[ -z "$ENV_NAME" ] && ENV_NAME=$(basename "$ENV_ID")

JOBS=0
if [ -n "$ENV_NAME" ]; then
    JOBS=$(az containerapp job list -g "$RG" -o json 2>/dev/null \
        | jq -r --arg e "$ENV_NAME" '[.[] | select((.properties.environmentId // "") | endswith($e))] | length' 2>/dev/null)
fi
# An unreadable or empty job list must not read as "jobs exist". Anything
# non-numeric collapses to 0 so the rule below fails loudly instead of passing
# on missing evidence.
case "$JOBS" in ''|*[!0-9]*) JOBS=0 ;; esac

PROVISIONING=$(jq -r '.properties.provisioningState // "Unknown"' <<<"$APP_JSON")

echo "GrandNode deployment check: $APP (rg: $RG)"
echo "  minReplicas=$MIN maxReplicas=$MAX jobs=$JOBS installer=${INSTALLER:-unset} redis=${REDIS:-unset}"
echo

# --- the app has to have started before any other rule means anything --------

if [ "$PROVISIONING" != "Succeeded" ]; then
    fail "container_app_provisioned" \
         "provisioningState is '$PROVISIONING', not 'Succeeded'. Every rule below is reported against an app that never started - check the image tag exists in the registry and read: az containerapp logs show -g $RG -n $APP"
else
    pass "container_app_provisioned"
fi

# --- the same four rules the Terraform module enforces -----------------------

# 1. installer needs a warm replica
if [ "$INSTALLER" = "true" ] && [ "$MIN" -lt 1 ]; then
    fail "installer_needs_a_warm_replica" \
         "The /install POST seeds the database in one request; at min_replicas=0 it cold-starts first and the browser times out. Set min_replicas=1 to install, then back."
else
    pass "installer_needs_a_warm_replica"
fi

# 2. background work has a home
if [ "$MIN" -lt 1 ] && [ "$JOBS" -eq 0 ]; then
    fail "background_work_has_a_home" \
         "min_replicas=0 with no Container Apps Jobs stops every scheduled task - queued email, unpaid-order expiry, auction endings, outbox drain - silently. Set min_replicas=1, or create jobs running 'dotnet Grand.Web.dll --run-task <name>'."
else
    pass "background_work_has_a_home"
fi

# 3. redis required for scale out
if [ "$MAX" -gt 1 ] && [ "$REDIS" != "true" ]; then
    fail "redis_required_for_scale_out" \
         "The cache is per-instance (RedisMessageCacheManager extends MemoryCacheBase; Redis carries only invalidation messages). Extra replicas serve stale prices and stock. Set Redis__RedisPubSubEnabled=true."
else
    pass "redis_required_for_scale_out"
fi

# 4. volumes must be seeded
if [ "$MOUNTS" -gt 0 ]; then
    warn "volumes_must_be_seeded_first" \
         "$MOUNTS volume mount(s) present. Verify the appdata share contains appsettings.json - an unseeded mount hides it and the host will not start: az storage file exists --share-name appdata --path appsettings.json"
else
    pass "volumes_must_be_seeded_first (no mounts)"
fi

# --- two settings the module applies that are easy to miss by hand -----------

if [ "$FORCE_HTTPS" != "true" ]; then
    fail "force_https_behind_ingress" \
         "Security__ForceUseHTTPS is not true. UseGrandForwardedHeaders never clears KnownNetworks/KnownProxies, so X-Forwarded-Proto from the Container Apps ingress (100.100.x.x) is discarded and antiforgery fails on every form post."
else
    pass "force_https_behind_ingress"
fi

if [ "$READINESS" = "/health/ready" ]; then
    fail "readiness_probe_not_latching" \
         "The readiness probe points at /health/ready, whose StartupHealthCheck is latched false by InstallController's ResetCache() on both success and failure. Running the installer removes the replica from rotation permanently. Use /health/live."
elif [ "$READINESS" = "none" ]; then
    warn "readiness_probe_not_latching" \
         "No readiness probe configured. Not dangerous, but traffic is routed before the app reports itself able to serve - a slow cold start will return errors rather than queue."
else
    pass "readiness_probe_not_latching (readiness=${READINESS}, liveness=${LIVENESS})"
fi

echo
if [ "$failures" -gt 0 ]; then
    echo "$failures rule(s) violated."
    exit 1
fi
echo "All rules pass."
