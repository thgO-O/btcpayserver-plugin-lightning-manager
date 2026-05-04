# BTCPayServer.Plugins.LightningWallet

External BTCPay Server plugin that adds a store-scoped Lightning wallet UI.

The plugin is intended for operators who already have a Lightning backend
configured in a BTCPay store and want basic wallet actions directly from the
BTCPay UI.

## Features

- `Overview`: node information, node URIs, on-chain balance, off-chain balance.
- `Send`: preview and pay fixed-amount BOLT11 invoices.
- `Peers`: connect to Lightning peers when the backend supports it.
- `Channels`: list channels and open channels when the backend supports it.

The UI is capability-driven. Backends that only support payments do not show
peer or channel actions.

## Requirements

- BTCPay Server `2.3.7` or newer.
- .NET `10.0` SDK for local development.
- A sibling BTCPay Server source tree at `../btcpayserver` when building from
  this repository.

## Provider Support

| Provider | Connection type | Overview | Send | Peers | Channels | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| LND | `lnd-rest`, `lnd-grpc` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. |
| Core Lightning | `clightning` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. |
| Eclair | `eclair` | Yes | Yes | Connect | List/open | Full-node workflow. Tested locally with Polar/regtest. Peer listing may be unavailable depending on the backend client. |
| Phoenixd | `phoenixd` | Yes | Yes | No | No | Payment-focused workflow. Phoenixd is not regtest-friendly; validate with a real network wallet. |
| Blink | `blink` | Limited | Yes | No | No | Payment-focused workflow. The Blink wallet network must match the BTCPay network. |
| LNbank | `lnbank` | Yes | Yes | Connect | List/open | Capability-mapped as full, but should be smoke-tested before advertising as production-ready. |

## v0.1 Scope

Included:

- Store-scoped navigation under the existing BTCPay Lightning menu.
- BOLT11 send flow with invoice preview.
- Friendly handling for invalid invoices, route failures, insufficient balance,
  and unknown payment status.
- Capability-based UI for full-node and pay-focused providers.
- Read-only behavior for non-admin users on the server shared internal
  Lightning node.

Not included in v0.1:

- Closing channels.
- Creating invoices or receiving funds from this UI.
- Amountless BOLT11 invoices.
- Payment history or plugin-owned persistence.
- LNURL, BOLT12, keysend, or spontaneous payments.

## Security Model

- The plugin does not persist its own state.
- The plugin reuses BTCPay store authorization.
- Read-only pages require BTCPay's store Lightning permission.
- Mutating actions, such as send, connect peer, and open channel, require
  BTCPay's store settings modification permission.
- The server shared internal Lightning node is read-only for non-admin users.
- Raw backend exception messages are not displayed in the UI.

## Installation

Use the packaged plugin artifact:

```text
artifacts/plugin-packages/BTCPayServer.Plugins.LightningWallet/0.1.0.0/BTCPayServer.Plugins.LightningWallet.btcpay
```

Install it through the BTCPay Server plugin UI or place it in the plugin
directory used by your deployment, then restart BTCPay Server.

After restart, the logs should include:

```text
Running plugin BTCPayServer.Plugins.LightningWallet - 0.1.0.0
```

## Mainnet Smoke Test

Before a public release, run a small-value smoke test on the final packaged
artifact:

1. Install the `.btcpay` package on a BTCPay Server instance.
2. Restart BTCPay Server and confirm the plugin loads.
3. Open a store with Lightning configured.
4. Open the Lightning Wallet page and confirm the provider capabilities match
   the table above.
5. Pay a small BOLT11 invoice with the provider under test.
6. Confirm the destination wallet received the payment.
7. Confirm the plugin only shows success when the payment result is settled.
8. Test an invalid BOLT11 invoice and confirm the UI shows a friendly error.

Recommended release sign-off:

- LND, Core Lightning, and Eclair: regtest smoke with Polar.
- Phoenixd and Blink: small-value real-network smoke focused on `Send`.

## Build

```bash
dotnet build BTCPayServer.Plugins.LightningWallet/BTCPayServer.Plugins.LightningWallet.csproj
```

Release build:

```bash
dotnet build BTCPayServer.Plugins.LightningWallet/BTCPayServer.Plugins.LightningWallet.csproj -c Release
```

## Test

```bash
dotnet test BTCPayServer.Plugins.LightningWallet.Tests/BTCPayServer.Plugins.LightningWallet.Tests.csproj
```

## Package

Build the plugin in Release mode first, then run the BTCPay plugin packer from
the sibling BTCPay Server repository:

```bash
dotnet ../btcpayserver/BTCPayServer.PluginPacker/bin/Release/net10.0/BTCPayServer.PluginPacker.dll \
  BTCPayServer.Plugins.LightningWallet/bin/Release/net10.0 \
  BTCPayServer.Plugins.LightningWallet \
  artifacts/plugin-packages
```

The package directory contains:

- `BTCPayServer.Plugins.LightningWallet.btcpay`
- `BTCPayServer.Plugins.LightningWallet.btcpay.json`
- `SHA256SUMS`
- `SHA256SUMS.asc`

The Release package should not contain `.pdb` files.
