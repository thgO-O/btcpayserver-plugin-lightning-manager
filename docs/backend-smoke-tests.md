# Backend smoke tests

Use this checklist against the final Plugin Builder artifact selected for
release. Record the BTCPay Server version, backend version and result for every
row. Never copy credentials or the full connection string into the report.

CLN and LND have automated service-level and browser coverage. Eclair 0.8 also
has browser coverage through BTCPay Server's native Playwright harness. Those
tests build the plugin from source; this checklist verifies the final artifact
and records backend sign-off. Do not interpret an exposed capability preset as
a successful sign-off.

The CLightning adapter currently selected by BTCPay Server 2.4.1 does not map
CLN's reported peer count. The automated CLN sign-off therefore excludes that
Overview field until the upstream fix is published and consumed by BTCPay.

Run each common check only when the backend exposes the corresponding
capability. Record unsupported checks as `N/A` in the sign-off notes.

## Common checks

- [ ] The plugin loads without warnings after BTCPay Server restarts.
- [ ] Only the capabilities documented in the README are shown.
- [ ] A fixed-amount invoice preview uses the signed amount.
- [ ] When supported by the backend, an amountless invoice accepts a positive
      whole-sat amount.
- [ ] For every payment exercised below, the destination reports the expected
      amount received: the signed amount for a fixed invoice or the entered
      amount for an amountless invoice.
- [ ] A malformed or expired invoice produces a generic error.
- [ ] Refreshing the result page does not submit the operation again.
- [ ] Backend errors and application logs contain no credentials, BOLT11,
      preimage or full payment hash.

## Eclair

- [ ] Info and balance load.
- [ ] A small fixed-amount and amountless payment settle with an explicit
      maximum fee.
- [ ] The destination reports the expected amount for both payments.
- [ ] Peer connection succeeds or returns a sanitized backend result.
- [ ] Existing channels are listed.
- [ ] A small channel-open request reaches Eclair with the entered sat/vB fee.
- [ ] A stale failed payment record is displayed as unknown, not definitively
      failed.

## Phoenixd

- [ ] Info and balance load.
- [ ] Pay is available; peer and channel actions are absent.
- [ ] The backend-fee-policy notice is shown and no max-fee value is submitted.
- [ ] A small fixed-amount and amountless payment settle.

## Blink custodial BTC (`api-key=` and `currency=BTC`)

- [ ] Balance and Pay are available; Info, peer and channel actions are absent.
- [ ] The backend-fee-policy notice is shown and no max-fee value is submitted.
- [ ] A small fixed-amount payment settles.
- [ ] An amountless invoice is rejected before dispatch with a friendly message.

## Blink custodial USD or legacy `api-key=` without `currency=`

- [ ] Only Pay is available.
- [ ] The backend-fee-policy notice is shown and no max-fee value is submitted.
- [ ] A small fixed-amount payment settles.
- [ ] An amountless invoice is rejected before dispatch with a friendly message.

## Blink receive-only (`ln-address=` or `username=`, without `api-key=`)

- [ ] Lightning Manager navigation and actions are absent.
- [ ] No outgoing payment is attempted through Lightning Manager.

## Sign-off record

| Backend | BTCPay version | Backend version | Date | Result | Notes |
| --- | --- | --- | --- | --- | --- |
| CLN | — | — | — | Not run | Native E2E exists; final artifact smoke pending. |
| LND | — | — | — | Not run | Native E2E exists; final artifact smoke pending. |
| Eclair | — | — | — | Not run | Native E2E exists; final artifact smoke pending. |
| Phoenixd | — | — | — | Not run | Release sign-off pending. |
| Blink custodial BTC | — | — | — | Not run | Release sign-off pending. |
| Blink custodial USD/legacy | — | — | — | Not run | Release sign-off pending. |
| Blink receive-only | — | — | — | Not run | Release sign-off pending. |
