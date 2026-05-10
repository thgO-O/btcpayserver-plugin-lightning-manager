# BTCPayServer.Plugins.LightningManager

External BTCPay Server plugin that adds Lightning management screens for store
operators and server admins.

The plugin supports two workflows:

- store-scoped management for stores with their own Lightning backend;
- shared internal-node operation, where the server admin controls the node and
  each store gets a separate accounting balance.

## Features

- `Overview`: node information, node URIs, on-chain balance, off-chain balance.
- `Pay`: per-store virtual balance for stores using the shared
  internal Lightning node.
- `History`: store-scoped Pay history with filters and pagination.
- `Send`: preview and pay fixed-amount BOLT11 invoices.
- `Peers`: connect to Lightning peers when the backend supports it.
- `Channels`: list channels and open channels when the backend supports it.
- `Server Lightning Manager`: server-admin view for the shared internal node,
  including node operations, store access, and balance adjustments.

The UI is capability-driven. Backends that only support payments do not show
peer or channel actions. For the shared internal node, store pages only show
store-scoped Pay and History data; node-wide balances, peers, and channels stay
in server-level views.

## Requirements

- BTCPay Server `2.3.7` or newer.
- .NET `10.0` SDK for local development.
- The BTCPay Server submodule initialized at `submodules/btcpayserver` when
  building from this repository.

## Provider Support

| Provider | Connection type | Overview | Send | Peers | Channels | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| LND | `lnd-rest`, `lnd-grpc` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. |
| Core Lightning | `clightning` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. |
| Eclair | `eclair` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. Peer listing may be unavailable depending on the backend client. |
| Phoenixd | `phoenixd` | Yes | Yes | No | No | Payment-focused workflow. Phoenixd is not regtest-friendly; validate with a real network wallet. |
| Blink | `blink` | Limited | Yes | No | No | Payment-focused workflow. The Blink wallet network must match the BTCPay network. |
| LNbank | `lnbank` | Yes | Yes | No | No | Payment-focused workflow. Smoke-test before advertising as production-ready. |

## v0.1 Scope

Included:

- Store-scoped navigation under the existing BTCPay Lightning menu.
- BOLT11 send flow with invoice preview.
- Friendly handling for invalid invoices, route failures, insufficient balance,
  and unknown payment status.
- Capability-based UI for full-node and pay-focused providers.
- Read-only behavior for non-admin users on the server shared internal
  Lightning node.
- Plugin-owned ledger tables for store-scoped internal node balances.
- Managed sends from Pay with reserve, settle, and release entries.
- Automatic invoice payment credits for internal-node stores.
- Automatic credits for native `BTC-LN` and `BTC-LNURL` payments on
  internal-node stores.
- Store-scoped History page with search, event/status filters, and pagination.
- Server-admin store access toggle for shared internal-node usage.
- Guard that keeps native `BTC-LN` and `BTC-LNURL` checkout disabled while the
  server admin has disabled store access.
- Server-admin balance adjustment for store accounting balances.

Not included in v0.1:

- Closing channels.
- Creating invoices or receiving funds from this UI.
- Amountless BOLT11 invoices.
- Custom LNURL checkout UI. The plugin credits BTCPay native `BTC-LNURL`
  payments, but it does not add a separate LNURL checkout button.
- BOLT12, keysend, or spontaneous payments.
- Unlocking BTCPay's native internal-node payout flow for non-admin users.
- Physical fund separation between stores on the Lightning node.

## Security Model

- The Pay ledger is accounting separation, not cryptographic or
  node-level fund separation.
- The plugin reuses BTCPay store authorization.
- Read-only pages require BTCPay's store Lightning permission.
- Mutating actions, such as send, connect peer, and open channel, require
  BTCPay's store settings modification permission.
- Shared internal-node access is a server-admin permission. Disabling access
  also excludes native `BTC-LN` and `BTC-LNURL` checkout for that store.
- Re-enabling access does not force checkout back on; the store owner can enable
  native Lightning settings again.
- The server shared internal Lightning node hides node-wide balances, channels,
  and global node actions from non-admin users.
- Pay sends reserve funds before paying and release reservations on
  known failures.
- Raw backend exception messages are not displayed in the UI.

## Internal Node Workflow

For a shared BTCPay internal Lightning node:

1. The server admin opens `Server Settings > Lightning Manager`.
2. The admin enables `Access` for the stores allowed to use the node.
3. The store owner can enable native Lightning checkout settings if desired.
4. Incoming `BTC-LN` and `BTC-LNURL` payments credit that store's Pay balance.
5. Store users send through `Lightning > Pay`; the plugin reserves the balance
   before paying and settles or releases the reservation afterward.
6. Store users review activity in `Lightning > History`.
7. The server admin manages node-wide send, peers, channels, access, and
   accounting adjustments from `Server Lightning Manager`.

The ledger is the plugin's accounting source of truth for store balances. It
does not segregate funds on the Lightning node.

## Installation

Use the packaged plugin artifact:

```text
artifacts/plugin-packages/BTCPayServer.Plugins.LightningManager/0.1.0.0/BTCPayServer.Plugins.LightningManager.btcpay
```

Install it through the BTCPay Server plugin UI or place it in the plugin
directory used by your deployment, then restart BTCPay Server.

After restart, the logs should include:

```text
Running plugin BTCPayServer.Plugins.LightningManager - 0.1.0.0
```

## Mainnet Smoke Test

Before a public release, run a small-value smoke test on the final packaged
artifact:

1. Install the `.btcpay` package on a BTCPay Server instance.
2. Restart BTCPay Server and confirm the plugin loads.
3. Open a store with Lightning configured.
4. Open the Lightning Manager page and confirm the provider capabilities match
   the table above.
5. Pay a small BOLT11 invoice with the provider under test.
6. Confirm the destination wallet received the payment.
7. Confirm the plugin only shows success when the payment result is settled.
8. Test an invalid BOLT11 invoice and confirm the UI shows a friendly error.

Recommended release sign-off:

- LND, Core Lightning, and Eclair: regtest smoke with Polar.
- Phoenixd and Blink: small-value real-network smoke focused on `Send`.

## Build

Initialize the BTCPay Server submodule first:

```bash
git submodule update --init --recursive
```

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj
```

Release build:

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj -c Release
```

## Test

```bash
dotnet test BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj -p:StaticWebAssetsEnabled=false
```

## Package

Build the plugin in Release mode first, then run the BTCPay plugin packer from
the sibling BTCPay Server repository:

```bash
dotnet submodules/btcpayserver/BTCPayServer.PluginPacker/bin/Release/net10.0/BTCPayServer.PluginPacker.dll \
  BTCPayServer.Plugins.LightningManager/bin/Release/net10.0 \
  BTCPayServer.Plugins.LightningManager \
  artifacts/plugin-packages
```

The package directory contains:

- `BTCPayServer.Plugins.LightningManager.btcpay`
- `BTCPayServer.Plugins.LightningManager.btcpay.json`
- `SHA256SUMS`
- `SHA256SUMS.asc`

The Release package should not contain `.pdb` files.
