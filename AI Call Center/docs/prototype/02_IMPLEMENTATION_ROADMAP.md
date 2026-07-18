# Prototype 1: Implementation Roadmap

## Phase overview

| Phase | Outcome | Depends on |
|---|---|---|
| 0. Baseline | Pinned tools, conventions, build entry points | ADR 0001 |
| 1. Backend skeleton | Buildable solution, hosts, modules, tests | Phase 0 |
| 2. Local infrastructure | Healthy PostgreSQL, Mosquitto, Valkey | Phase 0 |
| 3. Frontend and BFF | React loads and calls BFF health/session endpoints | Phases 1–2 |
| 4. Tenancy persistence | Synthetic tenant/location query through BFF | Phases 1–3 |
| 5. Outbox and MQTT | Committed update produces idempotent event flow | Phase 4 |
| 6. Realtime UI | Sanitized update refreshes Redux/RTK Query state | Phase 5 |
| 7. Hardening | Tests, traces, failure behavior, docs, CI | All prior phases |

## Phase 0 — Toolchain and conventions

Deliverables:

- `global.json` pins .NET SDK 10.0.302 with an intentional roll-forward policy.
- `.editorconfig` covers C#, TypeScript, JSON, YAML, and Markdown.
- `Directory.Build.props` enables nullable references, analyzers, deterministic builds, and the agreed warning policy.
- `Directory.Packages.props` becomes the central NuGet version source.
- Root developer commands and environment-variable conventions are documented.
- Secret and generated-output exclusions are complete.

Exit gate:

- Installed tool versions match ADR 0001.
- A placeholder-free validation command can verify formatting/configuration without application code.
- No environment-specific absolute path is committed.

## Phase 1 — Backend solution skeleton

Create only the projects required for the first slice:

- Building blocks: SharedKernel, Application Abstractions, Eventing, Observability, Testing.
- Hosts: WebBff, Api, Migrations, and one worker capable of outbox/integration-event publishing.
- Tenancy: Domain, Application, Infrastructure, Contracts, Presentation.
- Audit: minimal Domain/Application/Infrastructure/Contracts needed by the tenant update.
- Unit, integration, and architecture tests.

Relationships:

- Domain has no infrastructure/host dependencies.
- Application depends on Domain and approved abstractions.
- Infrastructure implements Application ports.
- Presentation invokes Application.
- Hosts compose dependencies without business rules.
- Tests enforce the project-reference graph.

Exit gate:

- `dotnet build` succeeds.
- Architecture tests reject a deliberately invalid dependency during test development, then pass after it is removed.
- Each executable exposes liveness without requiring all business dependencies.

## Phase 2 — Local infrastructure

Deliverables:

- Compose services for PostgreSQL, Mosquitto, and Valkey using ADR-pinned versions.
- Mosquitto development configuration with explicit listeners and no sensitive retained messages.
- Named volumes and service health checks.
- `.env.example` with synthetic non-production values; actual `.env` remains ignored.
- Start, stop, reset, inspect, and troubleshoot instructions.

Exit gate:

- `docker compose config` validates.
- All three services become healthy.
- Connectivity checks pass from the host and intended application network.
- Reset behavior is explicit and never targets broad host directories.

## Phase 3 — React and Web BFF boundary

Deliverables:

- React/TypeScript/Vite application.
- Redux Toolkit store, typed hooks, RTK Query base API, router, and app shell.
- BFF same-origin development proxy or documented origin configuration.
- Synthetic development session endpoint with explicit warning that it is not production authentication.
- BFF health/status response displayed by React.
- CORS, cookies, CSRF approach, and browser security headers documented and tested at the intended prototype level.

Exit gate:

- React loads successfully.
- Browser calls only BFF routes.
- A failed/expired synthetic session produces a stable unauthorized UI state.
- No token or sensitive data is stored in browser persistent storage.

## Phase 4 — Tenant/location query and command

Deliverables:

- Tenant and Location domain objects with invariants.
- Database schema/migrations owned by Tenancy.
- Seed command or migration for one synthetic tenant/location.
- `GetCurrentTenantSummary` query.
- Safe `UpdateLocationDisplayName` command with validation and optimistic concurrency.
- BFF response models shaped for the dashboard.
- Frontend tenant summary page and update workflow.
- Minimal append-only Audit record for the update.

Exit gate:

- Query flows React → BFF → Tenancy → PostgreSQL and back.
- Authorized update persists after process restart.
- Invalid, stale-version, and cross-tenant requests fail with stable error codes.
- Domain and integration tests cover the behavior.

## Phase 5 — Outbox and MQTT eventing

Deliverables:

- Standard event envelope and serializer registry.
- Outbox and inbox tables plus processing state.
- `LocationDisplayNameChanged` integration event contract and JSON schema.
- Outbox publisher using QoS 1 and tenant-scoped opaque-ID topic.
- Idempotent consumer that updates a simple operational projection or records verified receipt.
- Retry, cancellation, poison-message, and observability behavior.

Exit gate:

- Business state and outbox are committed atomically.
- Broker outage leaves messages pending and recovery publishes them later.
- Duplicate delivery produces one effective result.
- Invalid version/tenant payload is rejected safely.
- Contract schema and implementation agree.

## Phase 6 — Realtime dashboard update

Deliverables:

- BFF SSE or WebSocket endpoint; choose and record an ADR before implementation.
- BFF projection mapping that removes internal fields.
- Session/tenant/location authorization for every live connection/event.
- Frontend realtime service/middleware with reconnect and bounded backoff.
- RTK Query invalidation or normalized entity update.
- Re-query after reconnect to avoid relying on missed transient messages.

Exit gate:

- The dashboard updates after a committed location change without manual reload.
- Another tenant's synthetic event is never delivered to the session.
- Disconnect/reconnect restores correct authoritative state.
- Browser never receives internal MQTT credentials or unrestricted event envelopes.

## Phase 7 — Hardening and handoff

Deliverables:

- CI pipeline for format/build/test/contract/security checks.
- End-to-end happy path and key negative path.
- OpenTelemetry trace across the primary success scenario.
- Failure tests for database, broker, duplicate message, stale version, and realtime reconnect.
- Updated setup, architecture, troubleshooting, and demo documentation.
- Prototype limitations and next-phase risks recorded.

Exit gate:

- Final acceptance checklist passes from a clean environment.
- Demo script succeeds twice consecutively.
- No critical/high dependency or secret-scan findings remain unexplained.
- The team approves moving to Call Management design/implementation.

## What follows Prototype 1

The next prototype should implement the Call Management state machine with a simulated telephony adapter. Do not connect real telephony, AI, or Open Dental until call-state ownership, idempotency, observability, and tenant isolation are proven with simulators.

