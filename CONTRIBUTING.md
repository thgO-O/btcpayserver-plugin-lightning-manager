# Contributing

This document covers local development and validation for Lightning Manager.
Packaging and distribution are handled separately through the BTCPay Server
Plugin Builder and are not part of this repository's test gate.

## Prerequisites

- .NET `10.0` SDK.
- Docker, `jq`, and PowerShell (`pwsh`).
- The BTCPay Server submodule initialized at `submodules/btcpayserver`.

Initialize the submodule:

```bash
git submodule update --init --recursive
```

## Build and deterministic tests

Build the plugin:

```bash
dotnet build \
  BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj
```

Run the unit tests:

```bash
dotnet test \
  BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj
```

## BTCPay Server test stack

The end-to-end tests use the standard BTCPay Server regtest stack. The only
plugin-specific infrastructure is the Eclair 0.8 Docker Compose overlay.

The browser cases open real channels and therefore require disposable fixture
volumes. The following reset deletes only the BTCPay test-stack data, then
starts the official fixture and recreates Eclair:

```bash
cd submodules/btcpayserver/BTCPayServer.Tests
docker compose \
  -f docker-compose.yml \
  -f ../../../scripts/eclair-0.8.compose.yml \
  down --volumes
docker compose \
  -f docker-compose.yml \
  -f ../../../scripts/eclair-0.8.compose.yml \
  up -d dev
docker compose \
  -f docker-compose.yml \
  -f ../../../scripts/eclair-0.8.compose.yml \
  up -d --force-recreate --no-deps eclair
```

Return to the plugin root before running the tests:

```bash
cd ../../..
```

## Native end-to-end tests

The required service test and all four browser cases live in the xUnit v3 E2E
project. Run the complete project without a filter so a renamed trait cannot
silently remove a release test. Its native xUnit configuration also treats
skips as failures:

```bash
dotnet build \
  BTCPayServer.Plugins.LightningManager.E2ETests/BTCPayServer.Plugins.LightningManager.E2ETests.csproj \
  -c Debug
pwsh \
  BTCPayServer.Plugins.LightningManager.E2ETests/bin/Debug/net10.0/playwright.ps1 \
  install chromium
dotnet test \
  BTCPayServer.Plugins.LightningManager.E2ETests/BTCPayServer.Plugins.LightningManager.E2ETests.csproj \
  -c Debug --no-build
```

The CLN/LND service test prepares its direct channel through the native test
clients instead of relying on a shell channel-setup wrapper. It:

- funds CLN, connects it directly to LND, opens the channel, and mines it active;
- reconnects the peers idempotently;
- lists their existing channels through `LightningManagerService`;
- validates channel-opening previews without funding another channel; and
- settles payments in both directions and verifies the destination amount.

## Backend Playwright tests

The external CLN, LND, and Eclair browser cases plus the internal CLN case use
BTCPay Server's `UnitTestBase`, `ServerTester`, and `PlaywrightTester` instead
of maintaining a second application host or browser harness.

Each case creates its BTCPay user and store through the native harness, then
uses the browser to configure its backend, navigate Lightning Manager, connect
a peer, open and confirm a real regtest channel, and settle fixed and
amountless payments. Bitcoin RPC and the Lightning clients are used only to
prepare and verify backend state.

The internal CLN case configures the store with BTCPay Server's shared
`Internal` Lightning option and runs as a Server Admin with store Lightning
access. It also verifies the shared-node warning and confirms that a store
Owner who is not a Server Admin sees no menu item and is forbidden from direct
access.

Stop the test stack when finished:

```bash
cd submodules/btcpayserver/BTCPayServer.Tests
docker compose \
  -f docker-compose.yml \
  -f ../../../scripts/eclair-0.8.compose.yml \
  down --volumes
cd ../../..
```

## Validation scope

Repository validation consists of the Release build, deterministic tests,
the CLN/LND service integration test, and native Playwright cases for CLN, LND,
Eclair, and internal CLN. A test that needs its Lightning fixture must fail when
that fixture is unavailable; it must not be silently skipped.

The published CLightning adapter used by BTCPay Server 2.4.1 does not yet map
CLN's `num_peers`, so the CLN Overview peer count is excluded from automated
sign-off. All other CLN flows above remain covered. Restore the positive peer
count assertion when that upstream adapter fix reaches BTCPay Server.

This validation builds the plugin from source. Creating, installing, and
publishing the final `.btcpay` artifact stays in the Plugin Builder workflow.
Before publishing, run the manual checks below against that exact artifact and
BTCPay Server version.

## Manual backend sign-off

Use [`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md) as the
canonical checklist and sign-off record. It retains a final installation smoke
for internal CLN and Eclair and records the currently manual Phoenixd and Blink
validation. Never copy credentials into the record.

For an internal-node artifact smoke, verify both authorization boundaries: a
Server Admin with the store Lightning permission can manage the node, while a
non-admin store Owner cannot see Lightning Manager and receives a forbidden
response from a direct URL. Confirm that the page identifies the backend as
internal and warns that balances, payments, peers, and channels are shared
server-wide rather than isolated to the store.

## Manual channel-opening sign-off

Use disposable, funded regtest nodes for the final LND and CLN check:

1. Record both nodes' channel lists, pending-channel counts, and on-chain
   balances.
2. Connect the remote peer through Lightning Manager.
3. Preview a small channel at `1 sat/vB` and verify the peer, amount, and fee.
4. Submit the confirmation once.
5. Before mining, validate the backend-specific pending state:
   - For CLN, reopen `Channels` and confirm one additional channel is listed.
     On the node, confirm it does not yet have a short channel ID and verify the
     adapter sent `1000perkb` for the `1 sat/vB` input.
   - For LND, confirm that `Pending channels` in `Overview` and the node's
     pending-channel count each increased by one. Do not expect the channel in
     `Channels` before confirmation because the LND adapter does not include
     pending opens in its channel list.
6. Mine enough blocks for the configured channel confirmation threshold.
7. Verify exactly one funding transaction and one additional channel with the
   expected peer, capacity, and state.

Lightning Manager uses the Lightning adapter loaded by the BTCPay Server host;
it does not bundle a competing adapter. Run these checks against the exact
BTCPay Server version selected for release.
