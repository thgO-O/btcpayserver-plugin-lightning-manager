# Wallet Mode 0.2.0 validation

Validated locally on 2026-09-30, starting from `513ca4c`, with the pinned
BTCPay Server 2.4.4 submodule, .NET 10, PostgreSQL and the native regtest fixture.
The fixture used a separate disposable Docker Compose project.

| Check | Result |
| --- | --- |
| Plugin Release build | Passed, no warnings or errors |
| Deterministic tests, Release | 266 passed, no failures or skips |
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

## Remaining release checks

Physical Android and iPhone validation is pending. Follow the device checklist
in [CONTRIBUTING.md](../CONTRIBUTING.md): install/launch, login return, camera
permissions and scanning, real passkey enrollment/confirmation, logout and
offline cache inspection. The browser viewport and virtual authenticator
checks above do not establish physical-device compatibility.

Plugin Builder packaging and installation must be checked against the exact
artifact before distribution. No remote release or push is part of this local
validation record.
