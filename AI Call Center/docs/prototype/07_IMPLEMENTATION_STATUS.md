# Prototype 1 Implementation Status

Status date: 2026-07-18

## Proven locally

- The 22-project .NET/C# solution builds with zero warnings and errors.
- PostgreSQL 18, Mosquitto 2.1.2, and Valkey 8.1.8 are healthy in Docker Compose.
- EF Core migrations create `tenancy`, `audit`, and `eventing` schemas and seed synthetic data.
- A development-only BFF session scopes requests to one tenant and location.
- React 19 and Redux Toolkit Query read and update the location through the BFF.
- Optimistic concurrency protects updates.
- One transaction writes the location, audit record, and outbox message.
- The worker publishes pending events to MQTT with QoS 1.
- The BFF relays MQTT through a browser-safe SSE endpoint.
- A runtime test received `location-display-name-changed` over SSE after commit.
- Frontend production build succeeds; npm reported zero known vulnerabilities during install.

## Still open

- Production identity, authorization policies, cookies, CSRF, and security headers.
- Strict per-session filtering in the process-local SSE hub before multi-tenant use.
- Outbox leasing, inbox deduplication, dead letters, and broker outage automation.
- Broad domain, integration, frontend, and end-to-end automated tests.
- Full OpenTelemetry correlation.
- Open Dental, telephony, speech, and AI adapters.
- Valkey usage; it is provisioned but unused in this slice.

This is a runnable architecture prototype, not a production or HIPAA-ready call center.
