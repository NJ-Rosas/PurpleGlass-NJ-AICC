# ADR 0009: Provider-neutral telephony boundary

- Status: Accepted
- Date: 2026-07-25

## Context

PurpleGlass had a durable synthetic call lifecycle but no carrier transport. Direct Twilio references in CallManagement would make internal call identity, lifecycle, tests, and future carrier changes vendor-dependent. Carrier callbacks are duplicated, delayed, and out of order. Outbound side effects also cross a crash boundary where naive retries can create two real calls.

## Decision

CallManagement defines `ITelephonyProvider` and provider-neutral transport results. Vendor adapters implement that port outside module domain/application code. Task 8 supplies one real adapter, Twilio, plus disabled and deterministic fake adapters.

CallManagement persists normalized `TelephonyNumber` assignments, provider-qualified external identity on the call, callback receipts, and durable start/hangup operations. Internal `CallSessionId` remains authoritative. Public Twilio endpoints validate requests with the official helper before resolving tenant/location or changing lifecycle state.

The BFF records durable intent only. The integrations worker commits an operation as `Dispatching`, invokes the selected provider, then persists its safe result and provider call ID. Signed callbacks may reconcile an operation concurrently. Duplicate callback identity and conditional domain transitions provide webhook idempotency.

Because Twilio call creation has no documented client idempotency key, PurpleGlass never blindly retries an ambiguous `Dispatching` create operation. This is a conservative at-most-once submission decision. Known failures become sanitized operation/call failures; ambiguous operations require reconciliation rather than automatic replay.

Provider callbacks begin independent HTTP traces. Durable call and business-correlation identity links the work; W3C parentage is used only when actually propagated.

## Consequences

- CallManagement can change providers without vendor SDK changes.
- Disabled mode remains runnable without credentials or network access.
- Number routing, authorization, audit, outbox, MQTT, and SSE keep existing tenant/location boundaries.
- Twilio-specific signature, REST, status, and TwiML behavior stays isolated and contract-testable.
- The system prevents blind duplicate calls but does not yet automate ambiguous-dispatch reconciliation.
- Multi-provider failover, carrier provisioning, voice streaming, conversational AI, transfer, and campaigns remain future work.
