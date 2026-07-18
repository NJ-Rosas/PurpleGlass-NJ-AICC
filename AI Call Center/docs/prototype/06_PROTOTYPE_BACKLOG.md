# Prototype 1: Ordered Backlog

Items are ordered by dependency. `P0` is required for prototype completion; `P1` is valuable but may be deferred with an explicit decision.

## Epic 0 — Decisions

- [x] **P0** ADR: choose BFF realtime transport (SSE recommended for one-way prototype updates versus WebSocket).
- [x] **P0** ADR: choose development-session mechanism and ensure production configuration cannot enable it.
- [x] **P0** ADR: define module database/schema ownership and migration strategy.
- [x] **P0** ADR: define outbox/inbox ownership and processing model.
- [ ] **P0** Define ports, local URLs, and configuration naming conventions.

## Epic 1 — Toolchain and root configuration

- [x] **P0** Add `global.json` for .NET SDK 10.0.302.
- [x] **P0** Add `.editorconfig`.
- [x] **P0** Add `Directory.Build.props`.
- [x] **P0** Add `Directory.Packages.props`.
- [x] **P0** Expand `.gitignore` for .NET, Node, IDE, environment, coverage, and generated artifacts.
- [x] **P0** Add toolchain verification command/documentation.
- [ ] **P1** Add repository-wide formatting convenience command.

## Epic 2 — Backend skeleton

- [x] **P0** Create solution and initial project files.
- [x] **P0** Add project references matching architecture rules.
- [ ] **P0** Create SharedKernel typed-ID/result/time primitives only as required by the slice.
- [ ] **P0** Create Application abstraction interfaces required by the slice.
- [ ] **P0** Create Eventing and Observability registration foundations.
- [x] **P0** Create WebBff, Api, Migrations, and outbox worker hosts.
- [x] **P0** Create Tenancy layered projects.
- [x] **P0** Create minimal Audit projects required by the workflow.
- [x] **P0** Add unit, integration, and architecture test projects.
- [x] **P0** Make restore/build/test pass before domain expansion.

## Epic 3 — Local infrastructure

- [ ] **P0** Create Compose file with PostgreSQL, Mosquitto, and Valkey.
- [ ] **P0** Add Mosquitto development configuration.
- [ ] **P0** Add service health checks and named volumes.
- [ ] **P0** Add `.env.example` and ignored local `.env` pattern.
- [ ] **P0** Document start/stop/log/reset commands.
- [ ] **P0** Validate service connectivity from application processes.
- [ ] **P1** Add a local database administration UI only if it materially helps development; do not expose it by default.

## Epic 4 — Frontend and BFF shell

- [ ] **P0** Initialize React, TypeScript, and Vite.
- [ ] **P0** Install and configure Redux Toolkit, RTK Query, and router.
- [ ] **P0** Configure strict TypeScript, formatting, linting, and tests.
- [ ] **P0** Create app shell and accessible status/error presentation.
- [ ] **P0** Implement synthetic BFF session boundary.
- [ ] **P0** Implement BFF health/session endpoints.
- [ ] **P0** Configure browser-to-BFF development routing.
- [ ] **P0** Prove browser calls only BFF.

## Epic 5 — Tenancy domain and persistence

- [ ] **P0** Implement typed `TenantId` and `LocationId`.
- [ ] **P0** Implement Tenant and Location invariants needed by the slice.
- [ ] **P0** Implement Tenancy database context/configuration/repositories.
- [ ] **P0** Create initial migration.
- [ ] **P0** Create deterministic synthetic seed process.
- [ ] **P0** Implement tenant-scoped query.
- [ ] **P0** Implement display-name update with optimistic concurrency.
- [ ] **P0** Implement idempotency behavior.
- [ ] **P0** Implement minimal Audit record.
- [ ] **P0** Cover domain/application/persistence and isolation tests.

## Epic 6 — UI vertical slice

- [ ] **P0** Add tenant-summary RTK Query endpoint.
- [ ] **P0** Add settings page to display synthetic tenant/location.
- [ ] **P0** Add accessible display-name edit form.
- [ ] **P0** Handle loading, validation, unauthorized, forbidden, conflict, and dependency errors.
- [ ] **P0** Add frontend unit/component tests.
- [ ] **P0** Complete initial React → BFF → Tenancy → PostgreSQL flow.

## Epic 7 — Outbox and MQTT

- [ ] **P0** Implement standard event envelope.
- [ ] **P0** Create outbox/inbox persistence and migration.
- [ ] **P0** Define `LocationDisplayNameChanged` contract and schema.
- [ ] **P0** Write outbox message in the same transaction as the location update.
- [ ] **P0** Implement bounded outbox publisher.
- [ ] **P0** Configure tenant-scoped MQTT topic and QoS 1.
- [ ] **P0** Implement idempotent consumer/projection.
- [ ] **P0** Test broker outage, recovery, duplicates, invalid schema, and tenant mismatch.

## Epic 8 — Realtime BFF

- [ ] **P0** Implement selected authorized realtime endpoint.
- [ ] **P0** Map internal fact to sanitized browser event.
- [ ] **P0** Implement frontend realtime middleware/client.
- [ ] **P0** Invalidate/update RTK Query state.
- [ ] **P0** Add reconnect/backoff and authoritative re-query.
- [ ] **P0** Test cross-tenant non-delivery and reconnect recovery.

## Epic 9 — Observability and operations

- [ ] **P0** Propagate correlation/trace context.
- [ ] **P0** Add structured redaction-safe logs.
- [ ] **P0** Add HTTP/database/outbox/MQTT/consumer/realtime tracing.
- [ ] **P0** Add liveness/readiness endpoints and dependency checks.
- [ ] **P0** Add outbox, consumer, and realtime metrics.
- [ ] **P0** Write local troubleshooting runbook.

## Epic 10 — CI and acceptance

- [ ] **P0** Add restore/format/build/test CI pipeline.
- [ ] **P0** Add schema/contract compatibility validation.
- [ ] **P0** Add dependency and secret scanning.
- [ ] **P0** Add minimal end-to-end happy and negative paths.
- [ ] **P0** Execute clean-environment acceptance checklist.
- [ ] **P0** Execute demo twice successfully.
- [ ] **P0** Record limitations and Prototype 2 recommendations.

## Prototype completion marker

- [ ] All P0 items complete or explicitly waived by accepted ADR.
- [ ] Final acceptance checklist passes.
- [ ] Documentation matches the executable system.
- [ ] Prototype 2 work has not leaked into Prototype 1 without approval.
