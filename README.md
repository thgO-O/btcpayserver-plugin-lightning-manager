# BTCPayServer.Plugins.LightningWallet

External BTCPay Server plugin that adds a store-scoped Lightning wallet UI with:

- `Overview`
- `Send` for BOLT11 payments
- `Peers` for connect-only peer management
- `Channels` for channel listing and opening

The UI is capability-driven so pay-focused backends degrade cleanly:

- LND / Core Lightning / Eclair / LNbank: full MVP
- Phoenixd: overview + send
- Blink: pay-focused mode only

## Layout

- `BTCPayServer.Plugins.LightningWallet/BTCPayServer.Plugins.LightningWallet.csproj`
- `BTCPayServer.Plugins.LightningWallet.Tests/BTCPayServer.Plugins.LightningWallet.Tests.csproj`

## Build

This plugin project expects the sibling BTCPay Server source tree at `../btcpayserver`.

```bash
dotnet build BTCPayServer.Plugins.LightningWallet/BTCPayServer.Plugins.LightningWallet.csproj
```

## Test

```bash
dotnet test BTCPayServer.Plugins.LightningWallet.Tests/BTCPayServer.Plugins.LightningWallet.Tests.csproj
```

## Notes

- The plugin does not persist its own state.
- It reuses store-scoped BTCPay authorization and the existing `lightning-nav` UI extension point.
- The current implementation keeps capability mapping inside the plugin so the UI and controller flow remain backend-agnostic.
