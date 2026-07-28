# Lightning Manager for BTCPay Server

Lightning Manager adds BTC Lightning tools to BTCPay Server stores that use
their own external Lightning node or wallet.

From inside BTCPay Server, store operators can inspect the configured backend,
pay supported BOLT11 invoices, connect peers, and list or open channels when
the backend supports those actions.

> Lightning Manager works with external BTC Lightning connections only. It
> does not manage BTCPay's shared internal Lightning node.

## What You Can Do

- `Overview`: view node or wallet information, its address, and available
  balances.
- `Pay`: preview and pay fixed-amount BOLT11 invoices. Supported backends also
  accept a positive whole-sat amount for amountless invoices.
- `Peers`: connect to Lightning peers.
- `Channels`: list existing channels and open new channels.

The actions shown come from a capability preset selected from the external
connection string. For Blink, payment actions require `api-key=` and
`currency=` determines whether Balance is available. The credentials
configured in BTCPay Server still determine whether the backend authorizes
each request.

## Requirements

- BTCPay Server `2.4.1` or newer.
- A BTCPay store with an external BTC Lightning node or wallet already
  configured.
- The `Use the lightning nodes associated with your stores` permission.
- Lightning or on-chain funds for the actions you intend to perform.
- For LND management, an admin macaroon or a custom macaroon authorized for the
  required operations. The checkout-oriented `invoice.macaroon` is not enough
  for balances, outgoing payments, peers, or channels.

## Installation

1. Sign in to BTCPay Server as an administrator.
2. Open `Manage Plugins`, expand `Upload Plugin`, and upload the packaged
   `.btcpay` file from a source you trust.
3. Restart BTCPay Server to load the plugin.

## Quick Start

1. Open a store that already has external BTC Lightning configured.
2. Open the store's Lightning menu and select `Overview`.
3. Confirm that Lightning Manager detected the expected node or wallet and
   review the available actions.
4. Use `Pay` to preview an invoice before sending it.
5. Use `Peers` or `Channels` only when those tabs are available for your
   backend.

## Supported Lightning Setups

| Setup | Info | Balance | Pay | Amountless BOLT11 | Maximum fee | Connect peer | Open channel | List channels |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| LND REST / BTCPay `lnd-grpc` value | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Core Lightning (CLN) | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Eclair | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Phoenixd | Yes | Yes | Yes | Yes | No | No | No | No |
| Blink custodial with `api-key=` and `currency=BTC` | No | Yes | Yes | No | No | No | No | No |
| Blink custodial USD or legacy `api-key=` without `currency=` | No | No | Yes | No | No | No | No | No |
| Blink receive-only with `ln-address=` or `username=`, without `api-key=` | No | No | No | No | No | No | No | No |

LND connections use the REST client provided by BTCPay Server. The historical
`type=lnd-grpc` connection-string value is an alias for that REST client; it
does not select a separate gRPC transport.

The table describes the actions exposed by each connection-string preset; it
is not a runtime feature probe.

Blink receive-only connections do not expose Lightning Manager. They are
intended to receive payments, while this plugin does not create invoices or
provide receiving tools.

Peer and channel actions are available only for LND, CLN, and Eclair. Phoenixd
and Blink control routing fees through their own backend policy, so Lightning
Manager cannot set a per-payment maximum fee for them.

For fixed-amount invoices, the signed invoice amount is authoritative and any
submitted amount override is ignored. For amountless invoices on supported
backends, enter a positive whole number of satoshis before previewing the
payment. Blink custodial connections currently support fixed-amount invoices
only because the upstream adapter does not pass an operator-entered amount to
the wallet.

## Safety

- Payments and channel operations are sent directly to the configured
  Lightning node or wallet.
- Lightning Manager does not hold funds or maintain a separate balance ledger.
- A payment or channel action is shown for confirmation before submission.
- If the result of a submitted action is unknown, check the Lightning node or
  wallet before retrying.
- Completed payment and channel results are cached in memory for up to five
  minutes. This is not durable recovery; the Lightning node remains the source
  of truth.
- Test a new backend with a small payment before relying on it.

## Scope and Limits

Lightning Manager:

- supports BTC Lightning only;
- requires an explicit, supported `type=` in the external Lightning connection
  string;
- does not create a wallet, receive payments, create invoices, or change
  checkout behavior;
- does not support BOLT12, keysend, spontaneous payments, or LNURL checkout
  features;
- does not close channels, disconnect peers, rebalance, perform swaps, or
  change channel policies; and
- does not add a plugin-specific HTTP API, database, migrations, persisted
  settings, or custodial accounts.

## Troubleshooting

### Lightning Manager does not appear

Confirm that the store uses a supported external BTC Lightning connection and
that your user has the Lightning node access permission. The plugin is
intentionally hidden for BTCPay's shared internal node, non-BTC Lightning
configurations, unknown backends, and connection strings without a recognized
`type=`. It is also hidden for Blink receive-only connections without
`api-key=` because Lightning Manager does not provide receiving tools.

### A tab or action is missing

This normally means the capability preset for the connection string does not
include that action. For Blink, compare its `api-key=` and `currency=` fields
with the table above. For LND, also verify that the configured macaroon
authorizes the requested operation.

### An action was interrupted or its result is unclear

Reopen the same page and check the Lightning node or wallet before retrying.
The operation may have reached the backend even if the browser never received
the final response.

### Blink has limited overview information

Blink custodial connections with `api-key=` and `currency=BTC` expose Balance
and Pay but not node Info. Blink custodial USD and legacy connections without
an explicit `currency=` are payment-only. Blink receive-only connections
without `api-key=` do not expose Lightning Manager.

## Development

Build, test, package-validation, and backend sign-off instructions are in
[`CONTRIBUTING.md`](CONTRIBUTING.md). The manual backend checklist is in
[`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md).

## License

Lightning Manager is released under the [MIT License](LICENSE).
