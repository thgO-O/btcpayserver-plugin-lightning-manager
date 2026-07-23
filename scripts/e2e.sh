#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tests_project="$repo_root/BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj"

export LIGHTNING_MANAGER_E2E_CLN="${LIGHTNING_MANAGER_E2E_CLN:-type=clightning;server=tcp://127.0.0.1:30992/}"
export LIGHTNING_MANAGER_E2E_LND="${LIGHTNING_MANAGER_E2E_LND:-type=lnd-rest;server=http://lnd:lnd@127.0.0.1:35532/;allowinsecure=true}"
cln_probe="${LIGHTNING_MANAGER_E2E_CLN_PROBE:-127.0.0.1:30992}"
lnd_probe_url="${LIGHTNING_MANAGER_E2E_LND_PROBE_URL:-http://127.0.0.1:35532/v1/getinfo}"
lnd_probe_user="${LIGHTNING_MANAGER_E2E_LND_PROBE_USER:-lnd:lnd}"
cln_probe_host="${cln_probe%:*}"
cln_probe_port="${cln_probe##*:}"

if ! (exec 3<>"/dev/tcp/$cln_probe_host/$cln_probe_port") 2>/dev/null; then
    printf 'CLN test endpoint is unavailable on %s. Start the BTCPayServer.Tests regtest stack or override the probe variables.\n' "$cln_probe" >&2
    exit 1
fi

if ! curl --fail --silent --show-error --user "$lnd_probe_user" "$lnd_probe_url" >/dev/null; then
    printf 'LND REST test endpoint is unavailable at %s. Start the BTCPayServer.Tests regtest stack or override the probe variables.\n' "$lnd_probe_url" >&2
    exit 1
fi

dotnet test "$tests_project" \
    --filter 'Category=LightningManagerE2E' \
    --nologo \
    -m:1 \
    -nr:false \
    -p:UseSharedCompilation=false
