#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s <empty-package-output-directory>\n' "$0" >&2
    exit 2
fi

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$1"
if [[ "$output_dir" != /* ]]; then
    output_dir="$(pwd -P)/$output_dir"
fi
plugin_project="$repo_root/BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj"
tests_project="$repo_root/BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj"
packer_project="$repo_root/submodules/btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj"
packer_dll="$repo_root/submodules/btcpayserver/BTCPayServer.PluginPacker/bin/Release/net10.0/BTCPayServer.PluginPacker.dll"
btcpay_dir="$repo_root/submodules/btcpayserver"
expected_btcpay_commit="03345c2886a58ea4d2f603cb6554b2e3f5d690a4"

lightning_package_version() {
    local project="$1"
    sed -n 's/.*PackageReference Include="BTCPayServer.Lightning.All" Version="\([^"]*\)".*/\1/p' "$project"
}

validate_known_nu1608_warnings() {
    local assets_file="$1"
    local expected_html_sanitizer='Detected package version outside of dependency constraint: HtmlSanitizer 9.0.967 requires AngleSharp (= 0.17.1) but version AngleSharp 1.5.2 was resolved.'
    local expected_angle_sharp_css='Detected package version outside of dependency constraint: AngleSharp.Css 0.17.0 requires AngleSharp (>= 0.17.0 && < 0.18.0) but version AngleSharp 1.5.2 was resolved.'
    local warning_count
    local html_sanitizer_count
    local angle_sharp_css_count

    if [[ ! -f "$assets_file" ]]; then
        printf 'NuGet assets file was not generated: %s\n' "$assets_file" >&2
        exit 1
    fi

    warning_count="$(awk 'index($0, "\"code\": \"NU1608\"") { count++ } END { print count + 0 }' "$assets_file")"
    html_sanitizer_count="$(awk -v needle="$expected_html_sanitizer" 'index($0, needle) { count++ } END { print count + 0 }' "$assets_file")"
    angle_sharp_css_count="$(awk -v needle="$expected_angle_sharp_css" 'index($0, needle) { count++ } END { print count + 0 }' "$assets_file")"

    if [[ "$warning_count" -ne 2 ||
          "$html_sanitizer_count" -ne 1 ||
          "$angle_sharp_css_count" -ne 1 ]]; then
        printf 'Unexpected NU1608 warning set in %s. Only the two known BTCPay Server 2.4.1 AngleSharp conflicts are allowed.\n' \
            "$assets_file" >&2
        exit 1
    fi
}

cd "$repo_root"

if [[ ! -f "$btcpay_dir/BTCPayServer/BTCPayServer.csproj" ]]; then
    printf 'BTCPay Server submodule is not initialized. Run git submodule update --init --recursive.\n' >&2
    exit 1
fi

if [[ "$(git -C "$btcpay_dir" rev-parse HEAD)" != "$expected_btcpay_commit" ]]; then
    printf 'BTCPay Server submodule must be pinned to v2.4.1 for this release.\n' >&2
    exit 1
fi

if [[ -n "$(git -C "$btcpay_dir" status --porcelain)" ]]; then
    printf 'BTCPay Server submodule must have a clean worktree before packaging.\n' >&2
    git -C "$btcpay_dir" status --short >&2
    exit 1
fi

plugin_lightning_version="$(lightning_package_version "$plugin_project")"
tests_lightning_version="$(lightning_package_version "$tests_project")"
host_lightning_version="$(lightning_package_version "$btcpay_dir/BTCPayServer/BTCPayServer.csproj")"
if [[ -z "$host_lightning_version" ||
      -n "$plugin_lightning_version" ||
      "$tests_lightning_version" != "$host_lightning_version" ]]; then
    printf 'Lightning Manager production must inherit Lightning.All from BTCPay; the test runner reference must match the pinned host.\n' >&2
    printf 'Plugin direct ref: %s; tests direct ref: %s; host ref: %s\n' \
        "${plugin_lightning_version:-none}" \
        "${tests_lightning_version:-none}" \
        "${host_lightning_version:-missing}" >&2
    exit 1
fi

if [[ -e "$output_dir" ]]; then
    if [[ ! -d "$output_dir" || -n "$(ls -A "$output_dir")" ]]; then
        printf 'Package output must be a new or empty directory: %s\n' "$output_dir" >&2
        exit 1
    fi
else
    mkdir -p "$output_dir"
fi

empty_tree="$(git hash-object -t tree /dev/null)"
git diff --check "$empty_tree" HEAD -- . ':(exclude)submodules'
git diff --check
git diff --cached --check
while IFS= read -r -d '' untracked_file; do
    set +e
    untracked_check="$(git diff --no-index --check /dev/null "$untracked_file")"
    untracked_check_status=$?
    set -e
    if [[ "$untracked_check_status" -gt 1 ]]; then
        printf 'Could not check untracked file: %s\n' "$untracked_file" >&2
        exit "$untracked_check_status"
    fi
    if [[ -n "$untracked_check" ]]; then
        printf '%s\n' "$untracked_check" >&2
        exit 1
    fi
done < <(git ls-files --others --exclude-standard -z -- . ':(exclude)submodules')

dotnet restore "$tests_project" --nologo -m:1 -nr:false
validate_known_nu1608_warnings \
    "$repo_root/BTCPayServer.Plugins.LightningManager/obj/project.assets.json"
validate_known_nu1608_warnings \
    "$repo_root/BTCPayServer.Plugins.LightningManager.Tests/obj/project.assets.json"
dotnet clean "$plugin_project" -c Release --nologo -v:q -p:BuildProjectReferences=false -m:1 -nr:false
dotnet build "$plugin_project" -c Release --no-restore -p:TreatWarningsAsErrors=true --nologo -m:1 -nr:false
test_results_dir="$(mktemp -d "${TMPDIR:-/tmp}/lightning-manager-test-results.XXXXXX")"
trap 'rm -rf "$test_results_dir"' EXIT
dotnet test "$tests_project" \
    -c Release \
    --no-restore \
    -p:TreatWarningsAsErrors=true \
    --nologo \
    -m:1 \
    -nr:false \
    --results-directory "$test_results_dir" \
    --logger "trx;LogFileName=lightning-manager.trx"

test_results_file="$test_results_dir/lightning-manager.trx"
if [[ ! -f "$test_results_file" ]]; then
    printf 'The test runner did not produce the expected TRX result.\n' >&2
    exit 1
fi

total_tests=""
executed_tests=""
not_executed_tests=""
while IFS= read -r line; do
    if [[ "$line" == *"<Counters "* ]]; then
        if [[ "$line" =~ total=\"([0-9]+)\" ]]; then
            total_tests="${BASH_REMATCH[1]}"
        fi
        if [[ "$line" =~ executed=\"([0-9]+)\" ]]; then
            executed_tests="${BASH_REMATCH[1]}"
        fi
        if [[ "$line" =~ notExecuted=\"([0-9]+)\" ]]; then
            not_executed_tests="${BASH_REMATCH[1]}"
        fi
        break
    fi
done < "$test_results_file"

if [[ -z "$total_tests" || -z "$executed_tests" || -z "$not_executed_tests" ]]; then
    printf 'The TRX result does not contain valid execution counters.\n' >&2
    exit 1
fi
if [[ "$total_tests" -eq 0 ||
      "$total_tests" -ne "$executed_tests" ||
      "$not_executed_tests" -ne 0 ]]; then
    printf 'Release tests must execute without skips (total=%s, executed=%s, notExecuted=%s).\n' \
        "$total_tests" "$executed_tests" "$not_executed_tests" >&2
    exit 1
fi
printf 'Release tests verified without skips: %s/%s executed.\n' \
    "$executed_tests" "$total_tests"

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
    printf 'Release tests must contain exactly one passed %s result (found=%s, passed=%s).\n' \
        "$expected_e2e_test_name" "$e2e_test_count" "$e2e_passed_count" >&2
    exit 1
fi
printf 'Required CLN/LND end-to-end test passed.\n'

audit_output="$(dotnet list "$plugin_project" package --vulnerable --include-transitive --no-restore --format json)"
printf '%s\n' "$audit_output"
if [[ "$audit_output" == *'"severity"'* ]]; then
    printf 'Known vulnerable NuGet dependencies were found.\n' >&2
    exit 1
fi

dotnet restore "$packer_project" --nologo -m:1 -nr:false
dotnet build "$packer_project" -c Release --no-restore -p:TreatWarningsAsErrors=true --nologo -m:1 -nr:false
dotnet "$packer_dll" \
    "$repo_root/BTCPayServer.Plugins.LightningManager/bin/Release/net10.0" \
    BTCPayServer.Plugins.LightningManager \
    "$output_dir"

package_root="$output_dir/BTCPayServer.Plugins.LightningManager"
shopt -s nullglob
version_dirs=("$package_root"/*)
shopt -u nullglob
if [[ ${#version_dirs[@]} -ne 1 || ! -d "${version_dirs[0]}" ]]; then
    printf 'Expected exactly one packaged plugin version under %s.\n' "$package_root" >&2
    exit 1
fi

package_dir="${version_dirs[0]}"
package_file="$package_dir/BTCPayServer.Plugins.LightningManager.btcpay"
manifest_file="$package_dir/BTCPayServer.Plugins.LightningManager.btcpay.json"
checksums_file="$package_dir/SHA256SUMS"

for required_file in "$package_file" "$manifest_file" "$checksums_file"; do
    if [[ ! -f "$required_file" ]]; then
        printf 'Missing package output: %s\n' "$required_file" >&2
        exit 1
    fi
done

has_line() {
    local expected="$1"
    local contents="$2"
    local line
    while IFS= read -r line; do
        if [[ "$line" == "$expected" ]]; then
            return 0
        fi
    done <<< "$contents"
    return 1
}

checksum_entries="$(awk 'NF { print $2 }' "$checksums_file")"
checksum_entry_count="$(printf '%s\n' "$checksum_entries" | awk 'NF { count++ } END { print count + 0 }')"
if [[ "$checksum_entry_count" -ne 2 ]] ||
   ! has_line 'BTCPayServer.Plugins.LightningManager.btcpay' "$checksum_entries" ||
   ! has_line 'BTCPayServer.Plugins.LightningManager.btcpay.json' "$checksum_entries"; then
    printf 'SHA256SUMS must contain exactly the package and manifest entries.\n' >&2
    exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
    (cd "$package_dir" && sha256sum -c SHA256SUMS)
elif command -v gsha256sum >/dev/null 2>&1; then
    (cd "$package_dir" && gsha256sum -c SHA256SUMS)
else
    (cd "$package_dir" && shasum -a 256 -c SHA256SUMS)
fi

unzip -tq "$package_file"
archive_entries="$(unzip -Z1 "$package_file")"
entry_count="$(printf '%s\n' "$archive_entries" | awk 'NF { count++ } END { print count + 0 }')"
if [[ "$entry_count" -ne 2 ]]; then
    printf 'Unexpected package contents (%s entries):\n%s\n' "$entry_count" "$archive_entries" >&2
    exit 1
fi

for expected_entry in \
    BTCPayServer.Plugins.LightningManager.dll \
    BTCPayServer.Plugins.LightningManager.deps.json; do
    if ! has_line "$expected_entry" "$archive_entries"; then
        printf 'Package is missing expected entry: %s\n' "$expected_entry" >&2
        exit 1
    fi
done

manifest_contents="$(<"$manifest_file")"
expected_manifest='{"Identifier":"BTCPayServer.Plugins.LightningManager","Name":"Lightning Manager","Version":"0.1.0.0","Description":"BTC Lightning management UI for stores using external Lightning backends.","SystemPlugin":false,"Dependencies":[{"Identifier":"BTCPayServer","Condition":"\u003E=2.4.1"}]}'
if [[ "$manifest_contents" != "$expected_manifest" ]]; then
    printf 'Package manifest differs from the exact release metadata contract.\n' >&2
    exit 1
fi

printf 'Release package verified: %s\n' "$package_file"
