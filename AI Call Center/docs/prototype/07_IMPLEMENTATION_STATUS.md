# Prototype 1 Implementation Status

Status date: 2026-07-24

## Latest local validation

- The pinned toolchain was verified: .NET SDK 10.0.302, Node.js 24.18.0, npm 11.16.0, and Docker Compose 5.3.0.
- PostgreSQL, Mosquitto, and Valkey were started from `compose.yaml` and reported healthy.
- The migrations host applied the Eventing, Call Management, and Conversation migrations and confirmed the existing Tenancy seed.
- All 67 backend tests pass: 38 unit, 19 integration, and 10 architecture tests.
- The React production build succeeds.
- Both inbound and outbound console simulations completed with persisted `Completed` call and conversation states.

## Proven locally

- The .NET/C# solution builds with zero warnings and errors.
- PostgreSQL 18, Mosquitto 2.1.2, and Valkey 8.1.8 are healthy in Docker Compose.
- EF Core migrations create `tenancy`, `audit`, and `eventing` schemas and seed synthetic data.
- A development-only BFF session scopes requests to one tenant and location.
- React 19 and Redux Toolkit Query read and update the location through the BFF.
- Optimistic concurrency protects updates.
- One transaction writes the location, audit record, and outbox message.
- The worker publishes pending events to MQTT with QoS 1.
- The BFF relays MQTT through a browser-safe SSE endpoint.
- A runtime test received `location-display-name-changed` over SSE after commit.
- The process-local realtime hub filters MQTT events by the tenant in each BFF session.
- Unit tests cover realtime tenant isolation and MQTT topic validation.
- Frontend production build succeeds; npm reported zero known vulnerabilities during install.
- Call Management and Conversation now have durable PostgreSQL application paths with optimistic concurrency, idempotency, recording references, summaries, transcripts, and transactional outbox writes.
- The Call Orchestrator composes provider-neutral speech recognition, AI response generation, and speech synthesis ports without directly using module DbContexts.
- Deterministic mock AI and speech adapters support office facts, intake, two voices, urgent/human escalation, cancellation, timeouts, and failure simulation without external providers.
- A console simulator completes inbound and outbound multi-turn calls and prints persisted final states.
- PostgreSQL integration tests cover complete simulated flows, replay identity, failure cleanup, outbox facts, and adapter substitution.

## Still open

- Production identity, authorization policies, cookies, CSRF, and security headers.
- Outbox leasing, inbox deduplication, dead letters, and broker outage automation.
- Browser/dashboard delivery for the durable call and transcript facts.
- Frontend unit/component tests and a full browser end-to-end test suite.
- Continuous integration, contract compatibility checks, dependency scanning, and secret scanning.
- Full OpenTelemetry correlation.
- Open Dental and real telephony, speech, and AI provider adapters.
- Valkey usage; it is provisioned but unused in this slice.

## Recommended next milestone

Expose tenant-scoped durable call summaries and transcripts through the BFF and display them in the React dashboard with realtime refresh. Keep telephony, speech, and AI providers simulated until this browser-facing boundary, tenant isolation, event reliability, and observability are proven.

This is a runnable architecture prototype, not a production or HIPAA-ready call center.
