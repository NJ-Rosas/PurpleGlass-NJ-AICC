# Project Handoff and Next Steps

Status date: 2026-07-24

## Executive summary

PurpleGlass is a runnable, synthetic AI call-center architecture prototype for dental offices. It proves tenant-scoped persistence, auditing, transactional event delivery, realtime browser updates, durable call and conversation workflows, and provider-neutral AI/speech orchestration.

The system is not production-ready or HIPAA-ready. It does not yet have production authentication, live telephony, paid AI/speech providers, Open Dental connectivity, or real patient data.

## Work completed on `main`

The current `main` branch includes:

- A modular .NET backend with Tenancy, Audit, Call Management, Conversation, and Eventing boundaries.
- PostgreSQL persistence and EF Core migrations.
- Optimistic concurrency and idempotent call/conversation workflows.
- Transactional outbox messages and MQTT delivery.
- A development-only tenant/location session.
- A React and Redux Toolkit Query dashboard.
- Tenant-filtered MQTT-to-SSE realtime updates.
- Provider-neutral speech and AI application ports.
- Deterministic mock AI and speech adapters.
- Inbound and outbound console call simulations.
- Unit, integration, and architecture test projects.

## Completed work awaiting merge

### PR #1 — Reconcile prototype documentation

Pull request: <https://github.com/NJ-Rosas/PurpleGlass-NJ-AICC/pull/1>

Commit: `2c03ccf`

- Reconciles the backlog with the executable repository.
- Records infrastructure, migration, build, test, and simulator validation.
- Separates completed prototype capabilities from production hardening.

### PR #2 — Call dashboard and continuous integration

Pull request: <https://github.com/NJ-Rosas/PurpleGlass-NJ-AICC/pull/2>

Commit: `5fb5fc4`

- Adds tenant- and location-scoped recent-call and call-detail BFF endpoints.
- Returns persisted transcripts, summaries, outcomes, and escalation state.
- Adds a responsive recent-call and transcript-review experience to React.
- Adds realtime refresh for call and conversation MQTT events.
- Adds GitHub Actions validation for the backend and frontend.

Validation:

- 41 unit, 19 integration, and 10 architecture tests passed.
- The frontend production build passed.
- Both GitHub Actions jobs passed.
- Runtime BFF endpoints returned persisted simulator data.

### PR #3 — Event-delivery reliability

Pull request: <https://github.com/NJ-Rosas/PurpleGlass-NJ-AICC/pull/3>

Commit: `fa4cfc5`

- Adds bounded PostgreSQL outbox claims with `FOR UPDATE SKIP LOCKED`.
- Adds expiring leases and crashed-worker recovery.
- Adds exponential retry backoff and terminal dead letters.
- Adds Eventing-owned inbox receipts and execute-once deduplication.
- Adds outbox-leasing and inbox-persistence migrations.
- Documents operational inspection and reliability configuration.

Validation:

- 41 unit, 21 integration, and 10 architecture tests passed.
- Locked restore and the full build passed with zero warnings.
- A live MQTT outage preserved 37 pending messages.
- All 37 messages published automatically after MQTT recovered.

## Recommended merge and validation sequence

Merge in this order:

1. PR #1 — documentation reconciliation.
2. PR #2 — call dashboard and CI.
3. Update PR #3 from the new `main`, resolve overlap, and rerun CI.
4. Merge PR #3 after all checks pass.

Then validate from `AI Call Center`:

```bash
docker compose up -d
dotnet tool restore
dotnet restore src/backend/PurpleGlass.sln --locked-mode
dotnet run --project src/backend/Hosts/PurpleGlass.Migrations/PurpleGlass.Migrations.csproj
dotnet build src/backend/PurpleGlass.sln --no-restore
dotnet test src/backend/PurpleGlass.sln --no-build --no-restore
npm ci --prefix src/frontend
npm run build --prefix src/frontend
dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- inbound
dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- outbound
```

## Next milestones

### 1. Production security boundary

Complete this before live providers or real office/patient data:

- Select the production identity provider.
- Replace the synthetic session with authenticated server-side identity.
- Add tenant/location authorization policies.
- Configure secure, HTTP-only, same-site cookies.
- Add CSRF protection and browser security headers.
- Add authorization, cross-tenant, cookie, CSRF, and production-startup tests.
- Document secret storage, key rotation, expiration, and incident response.

### 2. End-to-end observability

- Add OpenTelemetry registration to the Observability building block.
- Propagate correlation through HTTP, database, outbox, MQTT, SSE, orchestration, and provider calls.
- Add redaction-safe structured logging.
- Add adapter-latency, call-duration, outbox-age, retry, dead-letter, inbox-duplicate, and realtime metrics.
- Configure optional OTLP export without committed credentials.
- Add trace and metric validation tests.

### 3. Dead-letter operations

- Add a safe operator view for dead-letter counts and metadata.
- Add alerts for outbox age and dead-letter growth.
- Define an audited replay workflow.
- Prevent replay of malformed, incompatible, or unauthorized messages.
- Add retention policies for published outbox and processed inbox rows.

### 4. Provider selection and readiness

Provider choices remain intentionally undecided:

- Select a telephony provider and review webhooks, audio streaming, transfers, recordings, and BAA support.
- Select AI and speech providers and review latency, retention, regional processing, safety, and BAA terms.
- Confirm the Open Dental deployment/interface, sandbox, authentication, rate limits, and appointment operations.
- Write ADRs for selected providers and fallback policies.
- Add contract fixtures and provider simulators before live connectivity.
- Store only managed-secret references in configuration or persistence.

### 5. Live provider adapters

Begin only after security, observability, vendor review, and sandbox access:

- Add a telephony webhook boundary with signature verification and replay protection.
- Implement telephony, speech, and AI adapters behind existing provider-neutral ports.
- Implement Open Dental through a Scheduling-owned application port.
- Add bounded timeouts, retries, rate limiting, circuit breaking, and safe fallbacks.
- Add sandbox contract tests and synthetic end-to-end tests.
- Keep production traffic disabled until security and compliance acceptance.

## Current risks and limitations

- The development session is not authentication.
- The prototype is not HIPAA-ready.
- Provider and Open Dental vendors are not selected.
- No real patient or production office data should be entered.
- Valkey is provisioned but unused.
- Full OpenTelemetry correlation is not implemented.
- Dead-letter alerting and operator replay are not implemented.
- Frontend unit/component and complete browser end-to-end coverage remain open.

## Immediate recommended action

Merge and validate PRs #1–#3, then implement the production security boundary before observability and provider integration. This prevents live external connectivity from bypassing authentication, tenant authorization, audit, and data-protection requirements.
