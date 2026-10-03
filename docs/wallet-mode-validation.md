# Wallet Mode 0.2.0 validation

Validated locally on 2026-09-30, starting from `513ca4c`, with the pinned
BTCPay Server 2.4.4 submodule, .NET 10, PostgreSQL and the native regtest fixture.
The fixture used a separate disposable Docker Compose project.

| Check | Result |
| --- | --- |
| Plugin Release build | Passed, no warnings or errors |
| Deterministic tests, Release | 274 passed, no failures or skips |
| Complete native E2E executable | 8 passed, no failures or skips |
| Whitespace / submodule changes | Clean diff; no core changes |

The eight E2E cases comprise the existing CLN/LND service integration, five
backend browser cases (CLN, LND, Eclair, internal CLN and internal LND), wallet
access/passkey/offline checks, and the PostgreSQL durability/concurrency case.
Wallet Mode is exercised in the four LND/CLN browser cases. Eclair continues
to exercise the Manager and is not offered Wallet Mode.

Wallet checks include receiving without a passkey, fixed and amountless
payments, actual receipt settlement, passkey cancellation, another account's
valid credential, altered invoice/amount/fee fields, replay, antiforgery,
store/node scoping, internal-node restrictions, and public-only offline cache.
PostgreSQL checks include repeatable migrations, 16 concurrent cross-store
claims, recovery after recreating storage, a write failure before sending,
and a simulated lost local result after backend settlement. Reconciliation
also works while Wallet Mode is disabled; duplicate hashes remain blocked.
Deterministic tests additionally exercise expiration, context binding and
30 concurrent consumers of a payment confirmation. Native LND and CLN adapter
tests verify stable identity with and without advertised addresses, including
the LND connection aliases, and reject an absent native public key.

The browser hosts start with BTCPay's default CSP enabled. The access case
opens and closes the real scanner, verifies service worker readiness without
CSP violations, and follows the offline retry link back to the wallet after
reconnecting. PostgreSQL coverage also verifies that confirmed settlement
replaces an older failure and stale updates cannot downgrade settlement or
erase its saved fee. The worker script changes so existing installations
reinstall their public assets with the updated offline page.

WebAuthn uses Chromium's virtual authenticator with user verification and
the actual BTCPay verifier. A fixture supplies no vendor metadata for that
virtual authenticator; no external FIDO metadata service is needed. This
test fixture is not packaged with Lightning Manager.

The follow-up recovery changes passed the complete eight-case E2E run with
PostgreSQL/regtest in `ln-wallet-review-v2-20260930` (277.175 seconds). In all
four LND/CLN browser cases, a database lock pauses submission, browser navigation
cancels the actual server request, and releasing the lock still leads to one
settled journal record and a paid peer invoice. These checks run through the
normal cookie, antiforgery and native passkey flow.

The PostgreSQL case additionally verifies Pending/Unknown replacing an older
failure, failed hashes staying discoverable, delayed backend settlement,
recovery of an abandoned Submitting record and settled-fee immutability. A
controlled native LND HTTP response verifies canceled invoices becoming expired
and backend unavailability preserving the persisted state. Signed BOLT11 invoices
come from the regtest peer; native invoice requests are checked for both values
of the private route-hint flag. The deterministic cases read the persisted
store blob and verify all four native LND invoice states, including ACCEPTED
past the invoice deadline.

The added request-abort observer and database gates belong only to the E2E
fixture. MailKit is referenced only by the unit test project so BTCPay's native
store-configuration types can be serialized/deserialized at runtime. No core
source, database schema or plugin dependency requirement changed.

## Latest amount and user-verification fixes

Validated in the separate disposable fixture `ln-wallet-v3-20260930`:
the complete eight-case E2E run passed in 158.129 seconds, with no failures or
skips. The final Release build passed without warnings or errors, and all 274
deterministic tests passed. After the final guard that preserves an already
settled row while its amount is unavailable, the PostgreSQL case was rebuilt
and rerun separately: one passed in 2.728 seconds. The Debug E2E rebuild emits
existing analyzer warnings in the pinned core test project; plugin compilation
has no errors. No core source was modified.

All four LND/CLN browser cases now submit a correctly signed assertion of the
current account's resident passkey with UV=false. The browser request alone is
relaxed to obtain that assertion; the native server challenge remains Required.
The server returns 400, the recipient invoice remains unpaid and no outgoing
journal row exists. Normal UV=true payments still succeed afterwards.

The same cases pay an amountless invoice for 500 sats through the Manager, then
attempt it through Wallet Mode for 1,000 sats. Wallet Mode rejects it before
creating a journal record, and the node's received amount remains 500 sats.
Recovery details display the native 500-sat settled amount while retaining and
flagging a different 1,000-sat reviewed amount.

The plugin-owned `20260930000200_SettledAmount` migration adds a nullable actual
settled amount without replacing the original reviewed amount. PostgreSQL
checks upgrade from the original wallet schema, stale-update protection for
settled amount/fee, backfill of legacy settled records, and preservation of
confirmed settlement when a lookup cannot yet supply the amount. Backfill
only queries the backend and never submits another payment. The required
E2E counter stays at eight because these assertions extend existing cases.

## Integration into main (2026-10-02)

The merge preserves the Manager's invoice URI normalization and QR scanner.
The CI executable now requires nine cases: the previous eight wallet/backend
cases plus the Manager QR scanner case. The shared LND route-readiness helper
also preserves the native query diagnostics added on main.

The merged tree passed the Release build without warnings or errors, all 277
deterministic tests, and the complete nine-case E2E executable in 171.172 seconds,
with no failures or skips. The Debug E2E build succeeded with existing analyzer
warnings from the pinned core test project. PostgreSQL, CLN, LND and Eclair ran
in the separate disposable Docker Compose project `wallet-merge-20261002`.
The running manual-test server and its fixture were left intact. Local test
results are saved under `output/wallet-merge-20261002/` and are not committed.

## Received invoice amounts (2026-10-02)

Incoming operations retain the requested invoice amount separately from the
actual received amount, using the existing settled-amount column. LND reads
its native `amt_paid_msat`; CLN supplies `AmountReceived`. Settled invoices
without a received amount remain reconcilable, including existing records.
An unavailable amount never downgrades confirmed settlement or replaces it
with the requested amount. A stale update cannot overwrite a known received
amount. No database migration is required.

The final Release and Debug builds passed without warnings or errors, all 283
deterministic tests passed, and the complete nine-case E2E executable passed
in 184.601 seconds without failures or skips. The run used fresh volumes in
the disposable `wallet-received-amount-20261002` fixture; results are saved in
`output/wallet-received-amount-20261002/` and are not committed.

Native adapter response tests cover 500 sats requested and 501 sats received
on LND and CLN, plus missing received amounts. PostgreSQL tests verify the
received amount, preservation of the requested amount, backfill of old receipts
and stale-update protection. All four LND/CLN browser cases receive a real
regtest payment, verify receipt/history amounts and backfill, and require the
receipt to reload when settlement arrives with reconciliation still pending.
The required CI case count remains nine because these extend existing cases.

## Remaining release checks

Physical Android and iPhone validation is pending. Follow the device checklist
in [CONTRIBUTING.md](../CONTRIBUTING.md): install/launch, login return, camera
permissions and scanning, real passkey enrollment/confirmation, logout and
offline cache inspection. The browser viewport and virtual authenticator
checks above do not establish physical-device compatibility.

Plugin Builder packaging and installation must be checked against the exact
artifact before distribution. No remote release or push is part of this local
validation record.
