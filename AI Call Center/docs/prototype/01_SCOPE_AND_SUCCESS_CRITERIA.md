# Prototype 1: Scope and Success Criteria

## Goal

Create a locally runnable, production-shaped skeleton demonstrating the platform's core boundaries: C#/.NET modules and hosts, React/Redux, BFF-only browser access, PostgreSQL persistence, transactional outbox, MQTT delivery, tenant isolation, and sanitized realtime UI updates.

This prototype proves architecture and development workflow. It is not a usable dental call center.

## Included scope

### Toolchain and repository

- Pin .NET SDK 10.0.302 and declare Node.js 24 LTS support.
- Establish central C# compiler/analyzer/package settings.
- Create a buildable .NET solution with initial hosts, building blocks, Tenancy module, and tests.
- Create a React/TypeScript application with Redux Toolkit and RTK Query.
- Provide repeatable developer commands and local environment documentation.

### Local infrastructure

- PostgreSQL 18 for authoritative tenant/location data and the outbox.
- Eclipse Mosquitto 2.1.2 for internal MQTT transport.
- Valkey 8.1.8 for a verified cache connection; business use may remain minimal.
- Docker Compose health checks, named volumes, isolated network, and non-production credentials.

### First business slice

- One synthetic tenant with one synthetic dental-office location.
- Tenant/location domain model with explicit identifiers and basic invariants.
- Tenant summary query exposed to React only through the Web BFF.
- One safe tenant/location update command.
- PostgreSQL persistence and optimistic concurrency.
- Transactional outbox record produced in the same transaction as the update.
- MQTT event published and processed idempotently.
- Sanitized realtime update delivered from BFF to React.
- Audit record for the update using a synthetic actor.

### Quality foundation

- Unit, integration, architecture, contract-boundary, and minimal browser/end-to-end tests.
- Structured logs, correlation IDs, traces, health, and readiness checks.
- Tenant-context validation at HTTP, Application, persistence, event, and realtime boundaries.
- No secrets or sensitive data in source control, logs, topics, or sample payloads.

## Explicitly excluded

- Telephony calls, audio streaming, speech-to-text, or text-to-speech.
- AI models, prompts, retrieval, conversation tools, or clinical/urgent-call behavior.
- Open Dental connectivity or real appointment/patient records.
- Authentication with a production identity provider; use a clearly marked synthetic development identity boundary.
- SMS/email delivery providers.
- Billing, payments, subscriptions, production analytics, or production infrastructure.
- Production deployment, availability guarantees, disaster recovery, or compliance certification.
- Real patient information, copied office data, recordings, transcripts, or production credentials.

Excluded items must not be represented by misleading mock production code. Use explicit interfaces and test simulators only where the prototype needs to prove a boundary.

## Primary success scenario

1. A developer clones the repository and follows the documented setup.
2. One command starts local infrastructure and application processes, or a small documented command set does so reliably.
3. The React dashboard loads through the local development entry point.
4. React requests the current tenant summary from the BFF.
5. The BFF resolves a synthetic authorized tenant context and calls Tenancy Application.
6. Tenancy reads PostgreSQL and returns only an approved contract.
7. The UI displays the synthetic tenant and location.
8. The user changes a safe field such as the location display name.
9. Tenancy validates and commits the change plus an outbox message atomically.
10. The outbox publisher emits a tenant-scoped MQTT event.
11. An idempotent consumer/projection processes the event.
12. The BFF sends a sanitized realtime update.
13. Redux/RTK Query refreshes the visible data.
14. Logs/traces show the shared correlation ID without exposing sensitive payloads.

## Measurable success criteria

| Area | Criterion |
|---|---|
| Setup | A clean developer environment can start the prototype using documented commands |
| Build | Backend and frontend builds complete without warnings treated as acceptable debt |
| Tests | Required automated suites pass locally and in CI |
| Browser boundary | Network inspection shows React calling only the BFF |
| Tenant isolation | Requests/events with a mismatched tenant are rejected and covered by tests |
| Persistence | Restarting application processes does not lose committed tenant/location state |
| Atomicity | A failed business transaction does not publish an event; a committed change eventually publishes |
| Idempotency | Reprocessing one event does not duplicate the projected effect |
| Realtime | A committed update appears in the UI without a manual reload |
| Observability | One workflow is traceable across BFF, Application, database/outbox, MQTT, and consumer |
| Security hygiene | No committed secrets or real patient data; MQTT topics contain opaque identifiers only |
| Documentation | Setup, architecture, troubleshooting, test, and demo steps match actual behavior |

## Definition of prototype complete

Prototype 1 is complete only when all mandatory backlog items are done, the final acceptance run passes from a clean environment, and the demo can be executed without editing code or database records manually.

