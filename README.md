# Lightning Manager for BTCPay Server

Lightning Manager adds BTC Lightning tools to BTCPay Server stores with a
configured Lightning node or wallet. It supports both store-specific external
connections and BTCPay Server's shared internal node.

From inside BTCPay Server, store operators can inspect the configured backend,
pay supported BOLT11 invoices, connect peers, and list or open channels when
the backend supports those actions.

> Managing the shared internal node is restricted to Server Admins who also
> have permission to use the Lightning node associated with that store. Store
> owners who are not Server Admins cannot see or open Lightning Manager for an
> internal-node store.

## What You Can Do

- `Overview`: view node or wallet information, its address, and available
  balances.
- `Pay`: preview and pay fixed-amount BOLT11 invoices. Supported backends also
  accept a positive whole-sat amount for amountless invoices.
- `Peers`: connect to Lightning peers.
- `Channels`: list existing channels and open new channels.

The actions shown come from a capability preset selected from the configured
backend. For Blink, payment actions require `api-key=` and `currency=`
determines whether Balance is available. The credentials configured in BTCPay
Server still determine whether the backend authorizes each request.

## Requirements

- BTCPay Server `2.4.1` or newer.
- A BTCPay store with an external BTC Lightning node or wallet, or the shared
  internal BTC Lightning node, already configured.
- The `Use the lightning nodes associated with your stores` permission.
- For the shared internal node, the user must also be a Server Admin. BTCPay's
  `AllowLightningInternalNodeForAll` setting does not grant access through
  Lightning Manager.
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

1. Open a store that already has BTC Lightning configured. For an internal-node
   store, sign in as a Server Admin who also has access to the store's
   Lightning node.
2. In the store sidebar, open `Plugins` and select `Lightning Manager`. This
   opens the BTC `Overview` page.
3. Confirm that Lightning Manager detected the expected node or wallet and
   review the available actions.
4. Use `Pay` to preview an invoice before sending it.
5. Use `Peers` or `Channels` only when those menu items are available for your
   backend.

## Capability Presets

| Setup | Info | Balance | Pay | Amountless BOLT11 | Maximum fee | Connect peer | Open channel | List channels | Validation status |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| LND REST / BTCPay `lnd-grpc` value / internal LND | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes | External Playwright E2E; internal factory coverage |
| Core Lightning (CLN), external or internal | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes | External + internal Playwright E2E; peer count blocked upstream |
| Eclair, external or internal | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes | External Playwright E2E (0.8); internal factory coverage |
| Phoenixd, external or internal | Yes | Yes | Yes | Yes | No | No | No | No | Manual sign-off pending |
| Blink custodial with `api-key=` and `currency=BTC` | No | Yes | Yes | No | No | No | No | No | Manual sign-off pending |
| Blink custodial USD or legacy `api-key=` without `currency=` | No | No | Yes | No | No | No | No | No | Manual sign-off pending |
| Blink receive-only with `ln-address=` or `username=`, without `api-key=` | No | No | No | No | No | No | No | No | Manual sign-off pending |

LND connections use the REST client provided by BTCPay Server. The historical
`type=lnd-grpc` connection-string value is an alias for that REST client; it
does not select a separate gRPC transport.

The Yes/No columns describe actions exposed by each backend preset; they are
not compatibility or release sign-off claims. Internal nodes reuse the preset
for their actual backend; an unknown internal backend is rejected. Repository
validation uses the standard BTCPay test stack for real service-level CLN/LND
integration and Playwright channel/payment flows for external CLN, internal
CLN, external LND, and external Eclair 0.8. Packaging and distribution are
handled separately through the Plugin Builder. Phoenixd and Blink remain
unvalidated until their manual records in
[`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md) contain real
backend evidence.

Blink receive-only connections do not expose Lightning Manager. They are
intended to receive payments, while this plugin does not create invoices or
provide receiving tools.

Peer and channel actions are available only for LND, CLN, and Eclair. Phoenixd
and Blink control routing fees through their own backend policy, so Lightning
Manager cannot set a per-payment maximum fee for them.

For fixed-amount invoices, the signed invoice amount is authoritative and any
submitted amount override is ignored. For amountless invoices on supported
backends, enter a positive whole number of satoshis before previewing the
payment. Backends without amountless support accept fixed-amount invoices only.

## Safety

- Payments and channel operations are sent directly to the configured
  Lightning node or wallet.
- Lightning Manager does not hold funds or maintain a separate balance ledger.
- The internal node is shared by the server. Its balance, payments, peers, and
  channels are server-wide and are not isolated to the store used to open
  Lightning Manager.
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
- requires either an explicit, supported `type=` in an external Lightning
  connection string or a supported internal Lightning backend;
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

Look for `Lightning Manager` under `Plugins` in the store sidebar. Confirm that
the store uses a supported BTC Lightning connection and that your user has the
Lightning node access permission. For the shared internal node, the user must
also be a Server Admin; enabling BTCPay's
`AllowLightningInternalNodeForAll` setting does not relax this requirement.
The plugin is hidden for non-BTC Lightning configurations, unknown backends,
external connection strings without a recognized `type=`, and Blink
receive-only connections without `api-key=`.

### The internal node is unavailable

Confirm that BTCPay Server has an internal BTC Lightning node configured and
that its backend is supported by the capability table above. Lightning Manager
does not expose the internal connection string or its credentials. If the
backend is not recognized, the plugin fails closed instead of attempting node
operations.

### Values are shared between stores

This is expected when stores select BTCPay Server's internal node. Lightning
Manager operates the node itself, so its balance, payments, peers, and channels
are server-wide. The plugin does not create a virtual balance or accounting
ledger for each store.

### A page or action is missing

This normally means the capability preset for the connection string does not
include that action. For Blink, compare its `api-key=` and `currency=` fields
with the table above. For LND, also verify that the configured macaroon
authorizes the requested operation.

### An action was interrupted or its result is unclear

Reopen the same page and check the Lightning node or wallet before retrying.
The operation may have reached the backend even if the browser never received
the final response.

## Development

Build, native BTCPay test-harness, and backend sign-off instructions are in
[`CONTRIBUTING.md`](CONTRIBUTING.md). The manual backend checklist is in
[`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md).

## License

Lightning Manager is released under the [MIT License](LICENSE).
