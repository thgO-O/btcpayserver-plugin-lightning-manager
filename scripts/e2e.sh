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

if ! curl \
    --fail \
    --silent \
    --show-error \
    --connect-timeout 5 \
    --max-time 10 \
    --user "$lnd_probe_user" \
    "$lnd_probe_url" >/dev/null; then
    printf 'LND REST test endpoint is unavailable at %s. Start the BTCPayServer.Tests regtest stack or override the probe variables.\n' "$lnd_probe_url" >&2
    exit 1
fi

test_results_dir="$(mktemp -d "${TMPDIR:-/tmp}/lightning-manager-e2e-results.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT INT TERM
    rm -rf "$test_results_dir"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

dotnet test "$tests_project" \
    --filter 'Category=LightningManagerE2E' \
    --nologo \
    -m:1 \
    -nr:false \
    -p:UseSharedCompilation=false \
    --results-directory "$test_results_dir" \
    --logger "trx;LogFileName=lightning-manager-e2e.trx"

test_results_file="$test_results_dir/lightning-manager-e2e.trx"
if [[ ! -f "$test_results_file" ]]; then
    printf 'The E2E test runner did not produce the expected TRX result.\n' >&2
    exit 1
fi

expected_e2e_test_name='BTCPayServer.Plugins.LightningManager.Tests.LightningManagerE2ETests.ClnAndLndManagementAndPaymentsWorkThroughProductionService'
e2e_test_count=0
e2e_passed_count=0
while IFS= read -r line; do
    if [[ "$line" == *"<UnitTestResult "* &&
          "$line" == *"testName=\"$expected_e2e_test_name\""* ]]; then
        e2e_test_count=$((e2e_test_count + 1))
        if [[ "$line" == *'outcome="Passed"'* ]]; then
            e2e_passed_count=$((e2e_passed_count + 1))
        fi
    fi
done < "$test_results_file"

if [[ "$e2e_test_count" -ne 1 || "$e2e_passed_count" -ne 1 ]]; then
    printf 'E2E tests must contain exactly one passed %s result (found=%s, passed=%s).\n' \
        "$expected_e2e_test_name" "$e2e_test_count" "$e2e_passed_count" >&2
    exit 1
fi
printf 'Required CLN/LND end-to-end test passed.\n'
