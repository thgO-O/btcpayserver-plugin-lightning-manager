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
- `Add funds to node`: generate an on-chain deposit address with QR code for LND, CLN, or Eclair.
- `Pay`: preview and pay fixed-amount BOLT11 invoices. Supported backends also
  accept a positive whole-sat amount for amountless invoices.
- `Channels`: list and open channels, connecting to the peer automatically on confirmation.
- `Channels → Manage peers`: connect or reconnect peers for troubleshooting.

The actions shown come from a capability preset selected from the configured
backend. For Blink, payment actions require `api-key=` and `currency=`
determines whether Balance is available. The credentials configured in BTCPay
Server still determine whether the backend authorizes each request.

## Requirements

- BTCPay Server `2.4.4` or newer.
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
5. Use `Channels` to open a channel: enter the peer URI, amount and fee, then
   review and confirm. The node connects to the peer before requesting the channel.
   Use `Manage peers` inside Channels for a separate connection or reconnection.

## Add funds before opening a channel

From Overview, select **Add funds to node** to display the deposit address and
QR code. Channels also offers this action when an opening attempt fails because
of insufficient on-chain balance. Send an on-chain Bitcoin payment to that address on the displayed
network. The address belongs to the configured Lightning node, not the store's
separate Bitcoin wallet. Wait for confirmations and check **Onchain Balance** in
Overview before opening a channel. Leave funds for transaction fees and the
node's required reserve.

This works with LND, CLN and Eclair, including supported internal nodes. For an
internal node, deposits fund the server's shared wallet and require the same
Server Admin access as the rest of Lightning Manager. Phoenixd and Blink must
be funded through their wallet or provider's own deposit tools; their adapters
do not expose an on-chain deposit address here.

## Capability Presets

| Setup | Info | Balance | Pay | Amountless BOLT11 | Maximum fee | Connect peer | Open channel | List channels | Validation status |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| LND REST / BTCPay `lnd-grpc` value / internal LND | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes | External + internal Playwright E2E |
| Core Lightning (CLN), external or internal | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes | External + internal Playwright E2E |
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
CLN, external and internal LND, and external Eclair 0.8. Packaging and distribution are
handled separately through the Plugin Builder. Phoenixd and Blink remain
unvalidated until their manual records in
[`docs/backend-smoke-tests.md`](docs/backend-smoke-tests.md) contain real
backend evidence.

Blink receive-only connections do not expose Lightning Manager. They are
intended to receive payments through checkout. Wallet Mode receiving tools are
available only for LND and Core Lightning.

Peer and channel actions are available only for LND, CLN, and Eclair. Phoenixd
and Blink control routing fees through their own backend policy, so Lightning
Manager cannot set a per-payment maximum fee for them.

For fixed-amount invoices, the signed invoice amount is authoritative and any
submitted amount override is ignored. For amountless invoices on supported
backends, enter a positive whole number of satoshis before previewing the
payment. Backends without amountless support accept fixed-amount invoices only.

## Wallet Mode (0.2.0)

Wallet Mode is an optional mobile PWA for LND and Core Lightning, including
internal nodes for Server Admins with store Lightning access. Open **Overview →
Wallet Mode settings**, enable it, and open **Wallet Mode**. Activation also
requires permission to modify the store. It is disabled by default.

The wallet shows the connected node's Lightning balance, pays fixed or amountless
BOLT11 invoices, creates one-hour receiving invoices using the store's private
route-hint setting, and displays the latest 100
Wallet Mode operations for the current store and node. Its history does not
include payments made elsewhere. This does not create individual accounts or
balances: operators authorized for the same node use the same funds. Reported
balance is not a guarantee of payment or inbound liquidity.

Sign in with your normal BTCPay account. Register a passkey in **Account →
Passkeys** before paying. Every PWA payment requires a separate passkey
confirmation bound to its invoice, amount, fee limit, user, store and node.
Confirmations expire after two minutes and can be used once. Without a passkey,
you can still receive and inspect the wallet. Wallet Mode does not reduce the
operator's existing Manager or Greenfield API permissions; those interfaces keep
their existing authentication.

Use your browser's **Install app / Add to Home Screen** action. On iPhone, open
the wallet in Safari and choose **Share → Add to Home Screen**. HTTPS is required
for production PWA and passkey functionality. The icon uses BTCPay's existing
artwork. Camera, passkey prompts and installation should be checked on the actual
device before release.

Only generic public assets and an offline message are cached. Balance, invoices,
history and authentication are never stored in the service worker cache. Offline
payments are not queued. The server and Lightning node must remain online;
receiving a payment does not require the phone to remain open. On LND, the
invoice's native state determines settlement and cancellation; an accepted
payment is not treated as expired just because its BOLT11 deadline passed.
Canceled invoices are displayed as expired. A lookup failure preserves the
last known state.

Before submitting, the wallet checks whether the node already knows the hash as
paid or in progress and rejects it without creating a new wallet operation.
The journal retains the reviewed amount separately from the actual settled amount
reported by the node. Details and history display the actual paid amount and
flag differences from the review, including a payment completed through another
interface during submission. An unavailable paid amount is shown as unavailable
and fetched again; it is never inferred from the reviewed amount. Existing
settled records are also backfilled without sending payments.

Wallet operations are persisted in plugin-owned PostgreSQL tables. A payment is
durably claimed before contacting the backend, including across stores connected
to the same node. A disconnect, timeout or server restart does not permit an
automatic resubmission. Pending or unknown outcomes are looked up on the backend
every 30 seconds and when viewing operation details. Failed outgoing hashes
are also checked: an older backend failure must not hide a later settlement.
A recent submission has a two-minute grace period before a lookup can report
a previous failure; abandoned submissions remain recoverable after a restart.
These lookups never send another payment. If the status remains
unknown, check the node before trying another payment. An already recorded
outgoing payment hash cannot be submitted again through Wallet Mode, including
after failure; create a fresh invoice for a deliberate retry.

Changing the node separates the history; changing credentials for the same node
does not. Disabling Wallet Mode preserves history and pending reconciliation.
Database initialization or availability failures block new wallet payments.
Keep the plugin's tables in your normal BTCPay database backup. They are an
operation journal, not a replacement for Lightning node backups.

LND/CLN Wallet Mode is covered by the native browser cases with WebAuthn virtual
authenticators and real regtest payments. Physical Android/iPhone installation,
camera and passkey validation remain required manual release checks.

## Manager Safety

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
- provides an optional wallet interface and BOLT11 receiving invoices without
  changing checkout behavior;
- does not support BOLT12, keysend, spontaneous payments, or LNURL checkout
  features;
- does not close channels, disconnect peers, rebalance, perform swaps, or
  change channel policies; and
- uses cookie-authenticated wallet UI endpoints and plugin-owned persisted
  settings, operation tables and migrations; it does not add a Greenfield API
  or custodial accounts.

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
