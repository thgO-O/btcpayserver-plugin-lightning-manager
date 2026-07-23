#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s <BTCPayServer.Plugins.LightningManager.btcpay>\n' "$0" >&2
    exit 2
fi

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
package_file="$1"
if [[ "$package_file" != /* ]]; then
    package_file="$(pwd -P)/$package_file"
fi

if [[ ! -f "$package_file" ]]; then
    printf 'Package not found: %s\n' "$package_file" >&2
    exit 1
fi

host_dll="$repo_root/submodules/btcpayserver/BTCPayServer/bin/Release/net10.0/BTCPayServer.dll"
if [[ ! -f "$host_dll" ]]; then
    printf 'Release BTCPay host not built. Run scripts/release-check.sh first.\n' >&2
    exit 1
fi

postgres_container="${LIGHTNING_MANAGER_SMOKE_POSTGRES_CONTAINER:-btcpayservertests-postgres-1}"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/lightning-manager-smoke.XXXXXX")"
plugin_identifier="BTCPayServer.Plugins.LightningManager"
plugin_dir="$temp_root/plugins/$plugin_identifier"
data_dir="$temp_root/data"
log_file="$temp_root/host.log"
database="lightning_manager_smoke_${RANDOM}_$$"
host_pid=""
database_created=false

cleanup() {
    local status=$?
    trap - EXIT INT TERM
    if [[ $# -eq 1 ]]; then
        status="$1"
    fi
    if [[ -n "$host_pid" ]] && kill -0 "$host_pid" 2>/dev/null; then
        kill "$host_pid" 2>/dev/null || true
        wait "$host_pid" 2>/dev/null || true
    fi
    if [[ "$database_created" == true ]]; then
        docker exec "$postgres_container" dropdb --if-exists -U postgres "$database" >/dev/null 2>&1 || true
    fi
    if [[ $status -ne 0 && -f "$log_file" ]]; then
        tail -n 120 "$log_file" >&2 || true
    fi
    rm -rf "$temp_root"
    exit "$status"
}
trap cleanup EXIT
trap 'cleanup 130' INT
trap 'cleanup 143' TERM

mkdir -p "$plugin_dir" "$data_dir"
unzip -q "$package_file" -d "$plugin_dir"

database_created=true
docker exec "$postgres_container" createdb -U postgres "$database"

(
    cd "$repo_root/submodules/btcpayserver/BTCPayServer"
    env \
        ASPNETCORE_ENVIRONMENT=Production \
        BTCPAY_NETWORK=regtest \
        BTCPAY_CHAINS=btc \
        BTCPAY_BIND=127.0.0.1 \
        BTCPAY_PORT=0 \
        BTCPAY_DATADIR="$data_dir" \
        BTCPAY_PLUGINDIR="$temp_root/plugins" \
        BTCPAY_POSTGRES="User ID=postgres;Host=127.0.0.1;Port=39372;Database=$database" \
        BTCPAY_BTCEXPLORERURL=http://127.0.0.1:32838/ \
        BTCPAY_RECOMMENDED-PLUGINS= \
        dotnet "$host_dll"
) >"$log_file" 2>&1 &
host_pid=$!

listening_url=""
for _ in $(seq 1 90); do
    if ! kill -0 "$host_pid" 2>/dev/null; then
        printf 'BTCPay host exited before the plugin smoke completed.\n' >&2
        exit 1
    fi

    listening_url="$(sed -n 's/.*Now listening on: \(http[^ ]*\).*/\1/p' "$log_file" | tail -n 1)"
    if [[ -n "$listening_url" ]] &&
       grep -Fq "Running plugin $plugin_identifier - 0.1.0.0" "$log_file"; then
        break
    fi
    sleep 1
done

if [[ -z "$listening_url" ]]; then
    printf 'BTCPay host did not start within 90 seconds.\n' >&2
    exit 1
fi

if ! grep -Fq "Running plugin $plugin_identifier - 0.1.0.0" "$log_file"; then
    printf 'BTCPay host started without loading Lightning Manager 0.1.0.0.\n' >&2
    exit 1
fi

curl --fail --silent --show-error "$listening_url/" >/dev/null
printf 'Package host smoke passed: %s\n' "$listening_url"
