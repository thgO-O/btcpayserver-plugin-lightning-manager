# Contributing

This document contains developer and release-validation instructions for
Lightning Manager. It covers local package creation and verification, not
publishing or distribution.

## Prerequisites

- .NET `10.0` SDK.
- The BTCPay Server submodule initialized at `submodules/btcpayserver`.
- Docker and the BTCPay Server regtest stack for package-smoke and end-to-end
  checks.

Initialize the submodule:

```bash
git submodule update --init --recursive
```

## Build and Test

Build the plugin:

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj
```

Run the test suite:

```bash
dotnet test BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj
```

## Automated Package Gate

Run the automated package gate with a new or empty output directory:

```bash
./scripts/release-check.sh /private/tmp/lightning-manager-release
```

The script performs:

- a Release build with warnings treated as errors;
- the standard test suite, with opt-in E2E checks skipped;
- a transitive NuGet vulnerability check;
- a PluginPacker build and package creation;
- checksum verification; and
- package-content and manifest validation.

CI calls the same script and does not publish or upload the resulting package.
This gate does not run the package-startup smoke, E2E tests, or manual backend
sign-off described below. It is pinned to the BTCPay Server `2.4.1` baseline,
whose Lightning adapter includes the corrected CLN fee-rate serialization and
pending-channel mapping.

## Package Startup Smoke

With the BTCPay Server regtest stack running, load the resulting package in an
isolated temporary BTCPay database and verify plugin startup plus HTTP:

```bash
./scripts/package-smoke.sh /private/tmp/lightning-manager-release/BTCPayServer.Plugins.LightningManager/0.1.0.0/BTCPayServer.Plugins.LightningManager.btcpay
```

The smoke test creates and drops only its uniquely named PostgreSQL database.
It does not stop or reset the shared regtest containers.

## CLN and LND End-to-End Test

Run the real bidirectional CLN/LND test against the BTCPay Server regtest
stack:

```bash
./scripts/e2e.sh
```

The stack must already have an active CLN/LND channel with at least 500 sats of
outbound liquidity in each direction. The script does not create, fund, or mine
a channel; missing channels or liquidity are reported as fixture failures.

The normal unit suite skips this test when the E2E environment is not enabled.
The script verifies the local CLN and LND endpoints before running only the
`LightningManagerE2E` category. The test:

- reconnects the two existing peers idempotently;
- maps their existing channels through the production service;
- validates channel-open previews without funding a channel; and
- sends equal-value payments in both directions.

Custom connection strings may be set with
`LIGHTNING_MANAGER_E2E_CLN` and `LIGHTNING_MANAGER_E2E_LND`. When the probe
endpoints differ from the local BTCPay test defaults, set
`LIGHTNING_MANAGER_E2E_CLN_PROBE`,
`LIGHTNING_MANAGER_E2E_LND_PROBE_URL`, and
`LIGHTNING_MANAGER_E2E_LND_PROBE_USER` as needed. If either node does not
advertise a reachable peer URI, set `LIGHTNING_MANAGER_E2E_CLN_NODE_URI` or
`LIGHTNING_MANAGER_E2E_LND_NODE_URI` to `pubkey@host[:port]`.

## Manual Backend Sign-Off

Before a public release, install the final `.btcpay` package on a BTCPay Server
instance and verify:

1. The plugin loads after restart.
2. The available actions match the configured node or wallet.
3. A small fixed-amount payment settles and reaches its destination.
4. Success is shown only after the payment settles.
5. LND, CLN, Eclair, and Phoenixd accept a positive whole-sat amount for an
   amountless invoice.
6. Blink rejects amountless invoices before dispatch.
7. A malformed or invalid invoice produces a friendly error.
8. Phoenixd and Blink show the backend-fee-policy warning and do not submit a
   maximum-fee value.

Automated sign-off currently covers LND and CLN peer connection, existing
channel listing, channel-open preview validation, and bidirectional payments.
It deliberately does not submit channel funding.

Use [`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md) to record the
manual Eclair, Phoenixd, Blink BTC, Blink USD, and legacy Blink results without
copying credentials.

## Manual Channel-Opening Sign-Off

Use disposable, funded regtest nodes for the final LND and CLN check:

1. Record both nodes' channel lists and on-chain balances.
2. Connect the remote peer through Lightning Manager.
3. Preview a small channel at `1 sat/vB` and verify the peer, amount, and fee.
4. Submit the confirmation once.
5. Before mining, reopen `Channels` and confirm exactly one pending channel is
   listed even when it does not yet have a short channel ID.
6. For CLN, verify the adapter sent `1000perkb` for the `1 sat/vB` input.
7. Mine a block and verify exactly one funding transaction and one new channel
   with the expected peer, capacity, and state.

Lightning Manager uses the Lightning adapter loaded by the BTCPay Server host;
it does not bundle a competing adapter. The functional checks above must
therefore be run against the exact BTCPay Server version selected for release.

## Manual Package Commands

The automated package gate is the preferred path. For isolated debugging,
build the plugin and PluginPacker in Release mode:

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj -c Release

dotnet build submodules/btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj -c Release
```

Then create a package:

```bash
dotnet submodules/btcpayserver/BTCPayServer.PluginPacker/bin/Release/net10.0/BTCPayServer.PluginPacker.dll \
  BTCPayServer.Plugins.LightningManager/bin/Release/net10.0 \
  BTCPayServer.Plugins.LightningManager \
  /private/tmp/lightning-manager-package
```

Always package into a new, empty output directory. PluginPacker replaces the
package and checksum files, but it does not remove unrelated files left by an
older release.

The package directory must contain:

- `BTCPayServer.Plugins.LightningManager.btcpay`;
- `BTCPayServer.Plugins.LightningManager.btcpay.json`; and
- `SHA256SUMS`.

The `.btcpay` archive must contain only the plugin DLL and `.deps.json`. It
must not contain `.pdb` files or an empty static-web-assets manifest left by
an older build.
