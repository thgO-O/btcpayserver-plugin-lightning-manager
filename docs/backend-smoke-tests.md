# Backend smoke tests

Use this checklist against the final packaged plugin. Record the BTCPay Server
version, backend version, connection-string `type` and result for every row.
Never copy credentials or the full connection string into the report.

## Common checks

- [ ] The plugin loads without warnings after BTCPay Server restarts.
- [ ] Only the capabilities documented in the README are shown.
- [ ] A fixed-amount invoice preview uses the signed amount.
- [ ] When supported by the backend, an amountless invoice accepts a positive
      whole-sat amount.
- [ ] A malformed or expired invoice produces a generic error.
- [ ] Refreshing the result page does not submit the operation again.
- [ ] Backend errors and application logs contain no credentials, BOLT11,
      preimage or full payment hash.

## Eclair

- [ ] Info and balance load.
- [ ] A small payment settles with an explicit maximum fee.
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
| Eclair | | | | | |
| Phoenixd | | | | | |
| Blink custodial BTC | | | | | |
| Blink custodial USD/legacy | | | | | |
| Blink receive-only | | | | | |
