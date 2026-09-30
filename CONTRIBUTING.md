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

## Dependency security

The submodule is pinned to BTCPay Server 2.4.4, which no longer references
SSH.NET. The plugin requires BTCPay Server 2.4.4 or newer at runtime as well.
NuGet audit and warnings-as-errors remain enabled without diagnostic exceptions.

BTCPay's Release projects still reference SourceLink 8.0.0. The repository's
`Directory.Build.targets` upgrades their private `Microsoft.Build.Tasks.Git`
build dependency to 10.0.303, a patched version for
[GHSA-23fw-v26w-5fgq](https://github.com/advisories/GHSA-23fw-v26w-5fgq).
This build-only dependency is not included in the plugin package. Keep the
build environment's .NET SDK updated too; changing the submodule does not
update the SDK or a deployed BTCPay Server.

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

The service test, PostgreSQL wallet test, access test and five backend browser cases live in the xUnit E2E
project. Run the complete project without a filter so a renamed trait cannot
silently remove a release test. Its native xUnit configuration also treats
skips as failures. Run its executable directly to use the xUnit runner without
changing the unit project's VSTest runner. Set `TEST_ECLAIR` to an Eclair connection
string when using a fixture on a different API port:

```bash
dotnet build \
  BTCPayServer.Plugins.LightningManager.E2ETests/BTCPayServer.Plugins.LightningManager.E2ETests.csproj \
  -c Debug
pwsh \
  BTCPayServer.Plugins.LightningManager.E2ETests/bin/Debug/net10.0/playwright.ps1 \
  install chromium
dotnet \
  BTCPayServer.Plugins.LightningManager.E2ETests/bin/Debug/net10.0/BTCPayServer.Plugins.LightningManager.E2ETests.dll \
  -failSkips
```

The CLN/LND service test prepares its direct channel through the native test
clients instead of relying on a shell channel-setup wrapper. It:

- funds CLN, connects it directly to LND, opens the channel, and mines it active;
- reconnects the peers idempotently;
- lists their existing channels through `LightningManagerService`;
- validates channel-opening previews without funding another channel; and
- settles payments in both directions and verifies the destination amount.

## Backend Playwright tests

The external CLN, LND, and Eclair browser cases plus the internal CLN and LND cases use
BTCPay Server's `UnitTestBase`, `ServerTester`, and `PlaywrightTester` instead
of maintaining a second application host or browser harness.

Each case creates its BTCPay user and store through the native harness, then
uses the browser to configure its backend, generate a node deposit address,
fund and confirm its on-chain wallet, and open a real regtest channel without
a separate peer connection. It then checks the secondary Manage peers action,
confirms the channel, and settles fixed and
amountless payments. Bitcoin RPC and the Lightning clients are used only to
prepare and verify backend state.

The internal CLN and LND cases configure the store with BTCPay Server's shared
`Internal` Lightning option and run as a Server Admin with store Lightning
access. They also verify the shared-node warning and confirm that a store
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

Wallet Mode extends the existing CLN/LND external and internal browser cases
with a mobile viewport, native passkey enrollment through a Chromium virtual
authenticator, fixed and amountless payments, receipt settlement, cancellation,
modified payment details, confirmation replay protection, and a persistent history.
An injected PostgreSQL write failure verifies no backend payment is submitted.
They simulate a lost local result after settlement, recreate storage, reconcile
while disabled and reject duplicate submission. The additional access case verifies
default-disabled activation, login/store authorization, a different account's
valid passkey, public manifest access and offline public-assets-only caching.
The PostgreSQL case checks repeatable migrations, cross-store
concurrent claims, recovery after recreating the repository, node/store scoping
and protection against overwriting terminal results. CI requires all eight cases
and treats skips as failures.

The test executable loads a metadata fixture for Chromium's virtual authenticator
to avoid calls to the external FIDO MDS. Signature, origin, challenge, user ownership
and user verification still run through BTCPay's real Fido2 verifier. This fixture
is never included in the plugin artifact.

Before releasing 0.2.0, manually check the exact installed artifact on Android
and iPhone: installation from the browser, standalone launch and return after
login, camera permission denied/granted, scan/cancel, passkey enrollment and
payment confirmation, logout, and offline behavior. Confirm no authenticated
HTML or financial responses appear in Cache Storage. Physical-device sign-off
is pending; virtual-authenticator tests do not replace it.

Repository validation consists of the Release build, deterministic tests,
the CLN/LND service integration test, and native Playwright cases for CLN, LND,
Eclair, internal CLN, and internal LND. A test that needs its Lightning fixture must fail when
that fixture is unavailable; it must not be silently skipped.

BTCPay Server 2.4.4 includes CLightning 1.7.7, which maps CLN's reported peer
and channel counts. Verify these Overview fields against the node during
manual sign-off.

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
2. Enter the remote peer URI directly in Channels, without connecting through Manage peers.
3. Preview a small channel at `1 sat/vB` and verify the peer, amount, and fee.
4. Submit the confirmation once.
5. Before mining, validate the backend-specific pending state:
   - For CLN, reopen `Channels` and confirm one additional channel is listed.
     On the node, confirm it does not yet have a short channel ID and verify the
     adapter sent `1000perkb` for the `1 sat/vB` input.
   - For LND, confirm that `Pending channels` in `Overview` and the node's
     pending-channel count each increased by one. In `Channels`, verify a
     `Pending` row with the peer, capacity, and funding outpoint, without usable
     liquidity. After confirmation, the same outpoint must appear once as `Active`.
6. Mine enough blocks for the configured channel confirmation threshold.
7. Verify exactly one funding transaction and one additional channel with the
   expected peer, capacity, and state.

Lightning Manager uses the Lightning adapter loaded by the BTCPay Server host;
it does not bundle a competing adapter. Run these checks against the exact
BTCPay Server version selected for release.
