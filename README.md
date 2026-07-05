# Lightning Manager for BTCPay Server

Lightning Manager adds extra BTC Lightning screens inside BTCPay Server for
stores that already use their own Lightning node or wallet.

Use it to check your Lightning setup, pay fixed-amount Lightning invoices, and,
when your node or wallet supports it, connect peers or open channels.

## Who This Is For

This plugin is for BTCPay store operators who already have BTC Lightning
configured with their own external Lightning node or wallet.

It is useful if you want to manage common Lightning actions from BTCPay instead
of switching to another node or wallet interface.

## Important Limits

- BTC Lightning only.
- External Lightning nodes or wallets only.
- It does not create a Lightning wallet for you.
- It does not use or manage BTCPay's shared internal Lightning node.
- It does not support Lightning for Litecoin or any other currency.
- It does not receive payments, create invoices, or change checkout behavior.
- It does not custody funds, credit invoices, or keep a store balance ledger.

## Requirements

- BTCPay Server `2.4.0` or newer.
- A BTCPay store with BTC Lightning already configured.
- A connected Lightning node or wallet with funds if you want to pay invoices.
- BTCPay permissions for the store:
  - Lightning node access to view the pages.
  - Store settings modification to pay invoices, connect peers, or open
    channels.

## What You Can Do

- `Overview`: view node information, node address, and available balances.
- `Pay`: preview and pay fixed-amount Lightning invoices with a maximum fee
  limit.
- `Peers`: connect to Lightning peers when your node supports it.
- `Channels`: list channels and open channels when your node supports it.

The plugin only shows actions your Lightning setup can support. For example, a
payment-focused wallet will show payment actions but not peer or channel
management.

## Supported Lightning Setups

| Setup | What works |
| --- | --- |
| LND | Overview, Pay, Peers, Channels |
| Core Lightning | Overview, Pay, Peers, Channels |
| Eclair | Overview, Pay, Peers, Channels |
| Phoenixd | Overview and Pay |
| Blink | Pay, with limited overview information |
| LndHub | Overview and Pay |

Peer and channel actions are only available for full Lightning nodes.
Payment-only wallets do not expose those screens.

## How To Use

1. Install the plugin and restart BTCPay Server.
2. Open a store that already has BTC Lightning configured.
3. Open the BTCPay Lightning menu.
4. Open `Overview` to confirm the detected node or wallet and available
   actions.
5. Use `Pay` to preview a Lightning invoice before sending the payment.
6. Use `Peers` or `Channels` only if those tabs are shown for your setup.

If your store uses BTCPay's shared internal Lightning node, Lightning Manager
will not appear for that store.

## Safety Notes

- Payments are sent directly by your connected Lightning node or wallet.
- The plugin does not hold funds.
- The plugin does not maintain a separate accounting balance.
- If a payment result is unknown, check your Lightning node or wallet before
  retrying.
- Raw backend errors are not shown in the UI.
- Before relying on a new backend, test with a small payment first.

## What Is Not Included

- Creating invoices or receiving Lightning payments.
- Lightning invoices without a preset amount.
- BOLT12, keysend, or spontaneous payments.
- LNURL checkout features.
- Closing channels.
- Shared internal-node balance management.
- Plugin-owned custodial balances or customer accounts.

## Installation

Install the packaged `.btcpay` plugin through the BTCPay Server plugin UI, then
restart BTCPay Server.

For local builds, the package is expected at:

```text
artifacts/plugin-packages/BTCPayServer.Plugins.LightningManager/0.1.0.0/BTCPayServer.Plugins.LightningManager.btcpay
```

After restart, the logs should include:

```text
Running plugin BTCPayServer.Plugins.LightningManager - 0.1.0.0
```

## Release Checklist

Before a public release, test the final `.btcpay` package on a BTCPay Server
instance:

1. Install the package through the plugin UI.
2. Restart BTCPay Server and confirm the plugin loads.
3. Open a store with BTC Lightning configured.
4. Confirm the shown actions match the node or wallet you are testing.
5. Pay a small fixed-amount Lightning invoice.
6. Confirm the destination wallet received the payment.
7. Confirm the plugin only shows success when the payment is settled.
8. Test an invalid Lightning invoice and confirm the UI shows a friendly error.

Recommended release sign-off:

- LND, Core Lightning, and Eclair: regtest smoke with Polar.
- Phoenixd, Blink, and LndHub: small-value real-network smoke focused on `Pay`.

## Development

Local development requires:

- .NET `10.0` SDK.
- The BTCPay Server submodule initialized at `submodules/btcpayserver`.

Initialize the submodule:

```bash
git submodule update --init --recursive
```

Build:

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj
```

Test:

```bash
dotnet test BTCPayServer.Plugins.LightningManager.Tests/BTCPayServer.Plugins.LightningManager.Tests.csproj
```

Release build:

```bash
dotnet build BTCPayServer.Plugins.LightningManager/BTCPayServer.Plugins.LightningManager.csproj -c Release
```

Package:

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

The release package should not contain `.pdb` files.
