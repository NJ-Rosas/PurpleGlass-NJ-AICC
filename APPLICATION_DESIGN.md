# AI Call Center Platform — Architecture and Pre-Coding Plan

> Status: Draft / source of truth before implementation  
> Initial vertical: Dental offices  
> Future direction: Multi-tenant, multi-industry AI communications platform  
> Core stack: .NET APIs/workers, React, TypeScript, Redux Toolkit, MQTT, BFF pattern  
> Initial dental system: Open Dental

## 1. Product Vision

Build an AI receptionist and call-center service that can answer and place calls, understand a caller's intent, perform approved business actions, transfer to a human, and keep a complete audit trail.

The first release targets dental offices and should support:

- Answering common questions such as office hours, location, accepted insurance, services, and preparation instructions.
- Identifying new and existing patients without exposing protected information.
- Booking, rescheduling, and canceling appointments through an approved scheduling integration.
- Capturing leads and appointment requests when live scheduling is unavailable.
- Routing urgent dental situations using office-approved scripts. The AI must not diagnose or replace emergency services.
- Transferring calls to office staff, an answering service, voicemail, or an emergency destination.
- Sending confirmation and follow-up messages when the tenant and patient have consented.
- Producing call summaries, transcripts, outcomes, action items, and operational analytics.

## 2. Guiding Architecture Principles

1. **Modular first, distributed when justified.** Start as a modular monolith plus independently deployable real-time/telephony workers. Keep module boundaries strict enough to extract services later.
2. **Domain logic is not in controllers, MQTT handlers, or React components.** Use application commands, queries, domain policies, and adapters.
3. **Events communicate facts; commands request work.** An event is past tense and immutable (`AppointmentBooked`). A command is imperative (`BookAppointment`).
4. **MQTT is not the system of record.** Durable business state lives in the relational database. MQTT distributes real-time commands, events, and status updates.
5. **Assume duplicate and out-of-order delivery.** Consumers must be idempotent. Events carry identifiers, timestamps, versions, tenant context, and correlation metadata.
6. **Tenant isolation is mandatory.** Every tenant-owned record, topic, cache key, log, trace, and authorization decision includes a tenant identifier.
7. **Compliance and safety are designed in.** Collect the minimum information required, encrypt sensitive data, audit access, define retention, and verify applicable legal/contractual requirements before production.
8. **Business verticals are replaceable modules.** Dental terminology and workflows implement shared platform contracts instead of leaking throughout the system.

## 3. Recommended Initial System Shape

Use a modular monolith for the management API and core business transactions, with separate workers for workloads having different scaling or failure characteristics.

| Deployable | Responsibility |
|---|---|
| Web App | Office dashboard, configuration, live calls, inbox, analytics, administration |
| Web BFF | The React-specific backend boundary. Handles the browser session, authorization context, UI-shaped queries/commands, aggregation, and sanitized realtime delivery |
| Platform API | Internal/application API for tenant administration, configuration, call records, appointments, and reporting; it is not called directly by the browser |
| Call Orchestrator Worker | Owns call session state and coordinates telephony, speech, AI, tools, transfers, and completion |
| Integration Worker | Consumes durable work and communicates with practice-management, calendar, messaging, CRM, and other external systems |
| Notification Worker | Sends SMS/email notifications through approved providers and records delivery status |
| MQTT Broker | Low-latency internal messaging and live dashboard updates; never exposed directly to browsers without a controlled gateway/token policy |
| Relational Database | Authoritative transactional data, tenant data, audit data, outbox/inbox tables |
| Cache | Short-lived session/configuration caching, distributed locks where unavoidable, rate-limit state |
| Object Storage | Call recordings and large transcript artifacts with tenant-aware access and retention policies |
| Observability Stack | Central logs, metrics, distributed traces, alerts, and audit reporting |

Do not split every domain into a networked microservice on day one. Extract a module only when it needs independent scaling, deployment, ownership, availability, or security isolation.

## 4. Context and Runtime Flow

Typical inbound-call flow:

1. Telephony provider reports an incoming call to the public webhook endpoint.
2. The webhook validates the provider signature and creates or finds an idempotent call session.
3. The call orchestrator loads tenant configuration, office hours, approved knowledge, routing policies, and voice/AI settings.
4. Audio is streamed through speech recognition and speech synthesis providers as required by the selected telephony design.
5. The AI can invoke only allow-listed tools with typed inputs, authorization policies, timeouts, and audit records.
6. Tool requests become application commands. Appointment and patient actions are validated by domain and tenant policies before an integration is called.
7. Call state and operational events are published. The dashboard receives a sanitized live projection, not unrestricted broker access.
8. On completion, the platform persists the outcome, summary, consent state, transcript/recording references, usage, and follow-up work.
9. Durable outbox messages drive integrations, notifications, analytics, and retries.

Typical dashboard flow:

1. React communicates only with the Web BFF using same-origin HTTPS and the selected realtime channel.
2. The BFF resolves the authenticated user, tenant, location, role, and permissions from the server-side session/context.
3. The BFF calls application modules/internal APIs and combines their results into UI-specific response models.
4. Commands from the UI pass through the BFF, but domain validation and authorization remain enforced by the owning backend module.
5. The BFF subscribes to internal events and sends only tenant-scoped, role-authorized, sanitized projections to React through SSE or WebSocket.
6. React never receives MQTT credentials and never calls Open Dental or another third-party system directly.

## 5. Proposed Repository Structure

```text
PurpleGlass/
├── APPLICATION_DESIGN.md
├── README.md
├── .editorconfig
├── .gitignore
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── docker-compose.yml
├── docs/
│   ├── adr/                         # Architecture Decision Records
│   ├── api/                         # API conventions and generated contracts
│   ├── events/                      # Event catalog and schemas
│   ├── compliance/                  # Retention, consent, access, incident plans
│   ├── runbooks/                    # Operations and incident procedures
│   └── threat-model/                # Data flows, assets, threats, mitigations
├── contracts/
│   ├── asyncapi/                    # MQTT/event documentation
│   ├── openapi/                     # Versioned public HTTP contracts
│   └── schemas/                     # JSON schemas and compatibility fixtures
├── src/
│   ├── backend/
│   │   ├── PurpleGlass.sln
│   │   ├── BuildingBlocks/
│   │   │   ├── PurpleGlass.SharedKernel/
│   │   │   ├── PurpleGlass.Application.Abstractions/
│   │   │   ├── PurpleGlass.Eventing/
│   │   │   ├── PurpleGlass.Observability/
│   │   │   └── PurpleGlass.Testing/
│   │   ├── Modules/
│   │   │   ├── Identity/
│   │   │   ├── Tenancy/
│   │   │   ├── CallManagement/
│   │   │   ├── Conversation/
│   │   │   ├── Scheduling/
│   │   │   ├── Contacts/
│   │   │   ├── Knowledge/
│   │   │   ├── Notifications/
│   │   │   ├── Billing/
│   │   │   ├── Analytics/
│   │   │   ├── Audit/
│   │   │   └── Verticals.Dental/
│   │   ├── Hosts/
│   │   │   ├── PurpleGlass.WebBff/
│   │   │   ├── PurpleGlass.Api/
│   │   │   ├── PurpleGlass.CallOrchestrator.Worker/
│   │   │   ├── PurpleGlass.Integrations.Worker/
│   │   │   ├── PurpleGlass.Notifications.Worker/
│   │   │   └── PurpleGlass.Migrations/
│   │   └── Adapters/
│   │       ├── Telephony/
│   │       ├── Speech/
│   │       ├── AI/
│   │       ├── PracticeManagement/
│   │       │   └── OpenDental/
│   │       ├── Messaging/
│   │       ├── Storage/
│   │       └── Payments/
│   └── frontend/
│       ├── package.json
│       ├── vite.config.ts
│       └── src/
│           ├── app/                 # Store, router, providers, app shell
│           ├── features/            # Redux feature slices and feature UI
│           │   ├── auth/
│           │   ├── calls/
│           │   ├── appointments/
│           │   ├── contacts/
│           │   ├── knowledge/
│           │   ├── analytics/
│           │   └── settings/
│           ├── entities/            # Shared normalized business entities
│           ├── pages/
│           ├── components/
│           ├── services/            # HTTP client, RTK Query, realtime client
│           ├── hooks/
│           ├── design-system/
│           ├── types/
│           └── test/
├── tests/
│   ├── unit/
│   ├── integration/
│   ├── architecture/
│   ├── contract/
│   ├── end-to-end/
│   ├── load/
│   └── security/
├── deploy/
│   ├── containers/
│   ├── local/
│   ├── environments/
│   └── infrastructure/              # Infrastructure as code
├── scripts/
└── samples/
```

### Backend module layout

Each business module should use the following internal structure unless a smaller module does not need every layer:

```text
ModuleName/
├── Domain/              # Aggregates, value objects, domain events, policies
├── Application/         # Commands, queries, handlers, ports, validation
├── Infrastructure/      # Persistence and external adapter implementations
├── Contracts/           # Public DTOs and integration events
└── Presentation/        # Minimal API endpoints or endpoint registration
```

Dependencies point inward: Presentation and Infrastructure depend on Application/Domain contracts; Domain does not depend on infrastructure, ASP.NET, EF Core, MQTT, or AI provider SDKs.

### BFF boundary

`PurpleGlass.WebBff` is a separately deployable ASP.NET Core host tailored to the React application. It may reference generated internal clients and BFF-specific composition code, but it must not become the owner of domain rules or directly manipulate module databases.

The BFF owns:

- Browser login/logout callbacks and secure session-cookie handling.
- CSRF protection, browser-facing rate limits, security headers, and CORS policy when needed.
- Resolving the authorized tenant/location context; the browser cannot grant itself tenant access by sending an arbitrary identifier.
- UI-oriented endpoints that aggregate or reshape data to avoid chatty frontend calls.
- A sanitized SSE/WebSocket stream for live-call and operational updates.
- Mapping backend errors into stable frontend error contracts.

The BFF does not own:

- Call, patient, appointment, scheduling, billing, or integration business rules.
- Open Dental credentials, synchronization logic, or record mappings beyond calling the owning application service.
- A second independent system of record or duplicated domain persistence.

Prefer one BFF for the web dashboard initially. Add a different BFF only if a future client—such as a mobile application or partner portal—has materially different security and data-composition needs.

## 6. Initial Domain Boundaries

### Platform modules

- **Identity:** Users, service identities, roles, authentication-provider mapping.
- **Tenancy:** Organizations, locations, subscriptions, tenant settings, feature flags.
- **Call Management:** Call sessions, participants, direction, state, transfer, disposition, recording references.
- **Conversation:** Turns, approved tools, tool executions, prompt/config versions, summaries, escalation.
- **Scheduling:** Availability, appointment requests, bookings, rescheduling, cancellation, and Open Dental synchronization status. PurpleGlass owns workflow/orchestration state; Open Dental is authoritative for imported patient appointment details and appointment timestamps.
- **Contacts:** Minimal caller/contact profile, communication preferences, consent, identity matching.
- **Knowledge:** Tenant-approved facts, documents, publication workflow, versions, retrieval metadata.
- **Notifications:** Message templates, delivery requests, provider results, opt-in/opt-out.
- **Billing:** Usage meters, plan limits, invoices/payment-provider references. Avoid storing raw card data.
- **Analytics:** Read models for outcomes, response times, conversion, transfers, failures, and costs.
- **Audit:** Append-only security and business audit events with actor, tenant, action, target, and reason.

### Dental vertical

The dental module supplies vocabulary, policies, workflows, and integration mappings for:

- Dental services and appointment types.
- Providers, operatories, locations, and duration rules.
- New-patient intake and referral source.
- Insurance-information capture without promising coverage.
- Office-approved urgent-call triage and escalation scripts.
- Pre-visit instructions and recall/reactivation campaigns.

Define a `BusinessVertical` contract so a future legal office, salon, or home-service business can replace these rules without changing the call engine.

### Open Dental integration boundary

Open Dental is the first implementation of the `IPracticeManagementSystem` port. Keep its API models, identifiers, terminology, authentication, and failure behavior inside `Adapters/PracticeManagement/OpenDental`; domain and frontend code use PurpleGlass contracts.

Authoritative-data rules:

- Open Dental is the source of truth for patient appointment details, appointment status, scheduled date/time, duration, provider/operatory association, and timestamps supplied by Open Dental.
- PurpleGlass stores the Open Dental record identifiers, a synchronized local projection needed for calls/search/display, and synchronization metadata. It must not silently overwrite newer Open Dental values.
- PurpleGlass remains authoritative for AI call sessions, conversation state, appointment-request workflow, tool executions, follow-up tasks, integration health, and audit history.
- Store appointment instants in UTC where an unambiguous instant is available, retain the dental office's IANA time-zone identifier, and retain the original Open Dental value/offset or source representation when needed for audit and conflict resolution.
- Never infer a tenant location's time zone from the server. Daylight-saving transitions, ambiguous local times, and office closure rules must be handled explicitly.

Required adapter operations for the MVP:

- Resolve a patient/contact using an office-approved identity-verification policy.
- Read patient appointment details and timestamps.
- Search appointment availability using supported Open Dental capabilities plus local tenant rules.
- Create, reschedule, and cancel appointments only when the configured Open Dental connection and office policy permit the action.
- Re-read the affected record after a mutation and persist the returned authoritative state.
- Support incremental reconciliation/polling and webhook/event ingestion if the selected Open Dental deployment and interface support it.

Every Open Dental request must include tenant-scoped connection resolution, timeout/cancellation, bounded retry behavior, rate limiting, correlation, audit, and sensitive-data redaction. Credentials belong in a managed secret store; only an encrypted secret reference belongs in the database.

Mutations require an idempotency record in PurpleGlass keyed by tenant, operation, target, and request key. Because an external system may time out after accepting a write, an unknown result must trigger reconciliation before retrying. Conflicts produce an operator-visible state; they are never resolved with an unconditional last-write-wins policy.

## 7. Core Data Model — First Draft

All tenant-owned entities include `TenantId`; location-specific entities also include `LocationId`. Use UTC timestamps and explicit time-zone identifiers for display and scheduling.

| Aggregate/entity | Key information |
|---|---|
| Tenant | Plan, status, default time zone, compliance configuration |
| Location | Address, time zone, office hours, closure calendar, routing policy |
| User / Membership | Identity-provider ID, tenant membership, role, status |
| Contact | Minimal identity/contact data, language, preferences, consent |
| CallSession | Provider call ID, direction, state, caller/callee, timestamps, outcome, correlation ID |
| Conversation | Call ID, configuration version, language, summary, escalation state |
| ConversationTurn | Speaker, time range, text reference, confidence, safety flags |
| ToolExecution | Tool/version, redacted input/output, authorization result, duration, outcome |
| Appointment | Open Dental appointment ID, patient/contact reference, type, provider/operatory, location, authoritative start/end/status, source timestamps, office time zone, sync version |
| AppointmentRequest | Requested windows, reason category, status, resolution |
| KnowledgeItem | Source, version, approval status, effective dates, tenant/location scope |
| ConsentRecord | Purpose, channel, status, source, timestamp, evidence reference |
| Notification | Channel, template/version, recipient reference, status, provider message ID |
| IntegrationConnection | Provider type (`OpenDental` initially), status, encrypted secret reference, scopes/capabilities, last sync, reconciliation cursor |
| OutboxMessage | Event payload, occurred time, publish status, retry metadata |
| InboxMessage | Consumer, message ID, processed time, result for idempotency |
| AuditRecord | Actor, action, resource, tenant, IP/context, reason, timestamp |
| UsageRecord | Tenant, call/session, provider/model, units, estimated/actual cost |

Separate highly sensitive patient data from operational projections when practical. Store recording/transcript blobs outside the relational database and keep only protected references and metadata.

## 8. MQTT and Eventing Design

### Broker use

MQTT is appropriate for call-state signaling, worker coordination, device/agent presence, and live dashboard updates. For business-critical work, combine it with a transactional outbox/inbox. If broker guarantees or operational needs later exceed MQTT's fit, preserve the event contracts and change the transport behind the eventing abstraction.

### Topic convention

```text
pg/{environment}/v1/tenants/{tenantId}/calls/{callId}/commands/{commandName}
pg/{environment}/v1/tenants/{tenantId}/calls/{callId}/events/{eventName}
pg/{environment}/v1/tenants/{tenantId}/integrations/{integration}/commands/{commandName}
pg/{environment}/v1/tenants/{tenantId}/notifications/commands/{commandName}
pg/{environment}/v1/tenants/{tenantId}/dashboard/events/{eventName}
pg/{environment}/v1/system/health/{serviceName}
```

Never place patient names, phone numbers, email addresses, or other sensitive values in topic names. Use opaque IDs.

### Standard event envelope

```json
{
  "messageId": "uuid",
  "messageType": "CallAnswered",
  "schemaVersion": 1,
  "occurredAtUtc": "2026-01-01T12:00:00Z",
  "tenantId": "uuid",
  "locationId": "uuid-or-null",
  "correlationId": "uuid",
  "causationId": "uuid-or-null",
  "traceId": "trace-id",
  "producer": "call-orchestrator",
  "dataClassification": "internal",
  "payload": {}
}
```

### Delivery rules

- Use QoS 1 for important commands/events and make every consumer idempotent.
- Retained messages are allowed only for non-sensitive current-state documents such as service availability; never retain calls, transcripts, or patient events.
- Configure per-service identities, TLS, topic-level ACLs, connection limits, expiration, payload size limits, and broker audit logs.
- Use dead-letter handling, bounded exponential retries with jitter, and an operations view for poison messages.
- Partition/order work by aggregate ID where ordering matters, while still protecting against stale versions.
- Publish only after the database transaction commits by using the outbox publisher.
- Document contracts with AsyncAPI and test backward compatibility in CI.

### Initial event catalog

Call events: `CallReceived`, `CallAnswered`, `CallStateChanged`, `IntentRecognized`, `HumanTransferRequested`, `CallTransferred`, `CallCompleted`, `CallFailed`.

Conversation events: `ToolExecutionRequested`, `ToolExecutionCompleted`, `EscalationTriggered`, `SummaryGenerated`.

Scheduling events: `AvailabilityRequested`, `AppointmentRequested`, `AppointmentBooked`, `AppointmentRescheduled`, `AppointmentCanceled`, `AppointmentSyncFailed`.

Notification events: `NotificationRequested`, `NotificationSent`, `NotificationDelivered`, `NotificationFailed`, `RecipientOptedOut`.

Integration events: `IntegrationConnected`, `IntegrationDisconnected`, `IntegrationSyncRequested`, `IntegrationSyncCompleted`, `IntegrationSyncFailed`.

Events represent facts and should not be renamed or changed incompatibly after publication. Add fields compatibly or publish a new schema version.

## 9. HTTP API Conventions

- Browser-facing routes are exposed by the BFF under `/bff/v1`. React does not call `/api/v1`, MQTT, or Open Dental directly.
- Internal/application endpoints use `/api/v1` and require service identity plus tenant context; do not treat network location as authorization.
- Prefix any future public/partner platform endpoints with `/api/v1`; version provider webhooks separately by adapter.
- Use OpenAPI as the HTTP contract and generate the TypeScript client where practical.
- Use Problem Details for errors and stable machine-readable error codes.
- Require an idempotency key for externally retried commands such as booking and notification requests.
- Support pagination, filtering, and explicit sort order for collections.
- Apply optimistic concurrency to mutable configuration and schedules.
- Validate at the boundary, authorize at both endpoint and domain-resource levels, and never trust a client-supplied tenant ID alone.
- Verify webhook signatures using the raw request body, enforce replay windows, and deduplicate provider event IDs.
- Keep health, readiness, and metrics endpoints separate from business endpoints and secure them appropriately.

If there is no external public API in the MVP, keep `/api/v1` private and expose only provider webhooks and the BFF at the edge.

## 10. React and Redux Design

Use React with TypeScript, Redux Toolkit, and RTK Query.

- Configure RTK Query with the same-origin BFF base path; components never embed internal-service or Open Dental URLs.
- RTK Query owns server data: calls, appointments, contacts, settings, and analytics.
- Redux slices own client/workflow state: selected tenant/location, filters, live call UI state, drafts, and non-server preferences.
- Normalize frequently updated live entities using entity adapters.
- A realtime middleware receives sanitized server-sent updates from the BFF over SSE or WebSocket and dispatches typed Redux actions or invalidates RTK Query tags.
- Do not connect the browser to MQTT. The BFF is the realtime gateway and enforces tenant, location, role, and field-level filtering.
- Keep feature components close to their slice/API definitions; keep generic presentation components in the design system.
- Make accessibility, keyboard navigation, responsive layouts, and localization part of the definition of done.
- Avoid storing access tokens or sensitive transcripts in persistent browser storage. Prefer secure cookies or the selected identity provider's recommended browser flow.

Example store shape:

```text
store
├── api                    # RTK Query cache
├── session                # Current user and authorized tenant/location context
├── liveCalls              # Normalized sanitized live-call projection
├── callWorkspace          # Selected call, panels, filters, transfer UI
├── appointmentWorkspace   # Calendar mode, selection, draft workflow
├── notifications          # UI notifications, not business messages
└── preferences            # Theme, locale, non-sensitive display preferences
```

## 11. AI and Conversation Runtime

Treat AI providers as replaceable adapters. Store a versioned conversation configuration for every call so behavior can be reproduced and audited.

### Required controls

- Version prompts, tool schemas, knowledge snapshots, model settings, voice settings, and safety policy.
- Use structured tool calling. The model proposes actions; application services validate and execute them.
- Give tools least privilege and tenant-scoped context. High-impact actions require explicit confirmation or a human approval policy.
- Protect against prompt injection in caller speech and retrieved documents. Retrieved text is data, not trusted instructions.
- Redact secrets and unnecessary sensitive values from model input, tool logs, telemetry, and error messages.
- Define confidence/failure thresholds and deterministic fallbacks: ask a clarifying question, capture a request, transfer, or end safely.
- Prevent medical diagnosis. Use office-approved language for urgent symptoms and instruct callers to contact emergency services when the configured policy requires it.
- Evaluate latency, interruption/barge-in, silence, background noise, accent/language behavior, tool accuracy, hallucination rate, transfer success, and cost.
- Maintain provider timeouts, circuit breakers, concurrency limits, and a fallback strategy for speech/AI outages.

### Tool examples

`GetOfficeHours`, `GetOfficeLocation`, `SearchApprovedKnowledge`, `CheckAvailability`, `CreateAppointmentRequest`, `BookAppointment`, `RescheduleAppointment`, `CancelAppointment`, `SendConfirmation`, `TransferCall`, `CreateStaffFollowUp`.

Each tool definition needs: owner, purpose, input/output schema, permissions, validation, audit policy, timeout, idempotency behavior, failure wording, and compensation/recovery procedure.

## 12. Security, Privacy, and Compliance Design

Before production, obtain qualified legal/security review for the operating regions and customer contracts. Dental data may fall under HIPAA in the United States and other privacy/recording laws depending on jurisdiction and use. Compliance is a system-wide operating practice, not a framework checkbox.

### Minimum controls

- Map every data flow and classify data as public, internal, confidential, or regulated/sensitive.
- Confirm required agreements with telephony, AI, speech, hosting, messaging, monitoring, storage, and integration vendors, including BAAs where applicable.
- Document call-recording and AI-disclosure consent rules per jurisdiction and tenant; play tenant-approved notices before recording when required.
- Encrypt in transit and at rest; use a managed secret store and per-environment keys. Never commit secrets.
- Use OIDC/OAuth, MFA for staff, role- and resource-based authorization, short-lived service credentials, and least privilege.
- Enforce tenant isolation in the application and database. Add automated cross-tenant access tests.
- Maintain immutable audit trails for sensitive reads, writes, exports, configuration changes, tool executions, and impersonation/support access.
- Define transcript, recording, log, backup, and audit retention separately. Automate deletion and legal-hold exceptions.
- Provide data export, correction, deletion, consent withdrawal, and access-reporting workflows where applicable.
- Mask sensitive values in UI, logs, support tools, traces, analytics, and non-production datasets.
- Perform dependency scanning, secret scanning, SAST, container scanning, threat modeling, penetration testing, backup-restore exercises, and incident-response drills.
- Define breach/incident notification responsibilities, contacts, evidence preservation, and customer communication procedures.

## 13. Reliability and Operations

Define service-level objectives before launch. Initial targets should cover call answer success, time to first audio, conversational latency, booking success, transfer success, event processing delay, and dashboard availability.

Operational requirements:

- Structured logs with tenant/correlation IDs but no unnecessary sensitive payloads.
- OpenTelemetry traces across webhooks, API, MQTT, database, integrations, and providers.
- Metrics for call states, provider latency/errors, dropped audio, AI tokens/cost, tool outcomes, MQTT backlog/reconnects, outbox age, retries, and dead letters.
- Readiness checks verify critical dependencies without exposing secrets; liveness checks detect a stuck process.
- Graceful shutdown drains active calls/messages within bounded limits.
- Per-tenant and per-provider rate limits, quotas, budgets, and anomaly alerts.
- Tested backups, point-in-time recovery where appropriate, documented RPO/RTO, and disaster-recovery exercises.
- Runbooks for telephony outage, AI/speech outage, broker outage, scheduling-integration outage, suspected data exposure, and runaway cost.

## 14. Testing Strategy

- **Unit tests:** Domain policies, state machines, validators, reducers, selectors, and pure conversation rules.
- **Architecture tests:** Enforce module boundaries and prohibited dependencies.
- **Integration tests:** Database, MQTT broker, cache, object storage emulator, webhook validation, outbox/inbox behavior.
- **Contract tests:** OpenAPI clients, AsyncAPI/JSON schemas, provider webhooks, and practice-management adapters.
- **Conversation evaluations:** Golden scenarios with expected intent, tool call, safety behavior, and response constraints.
- **End-to-end tests:** Inbound call simulation through booking/transfer and dashboard update.
- **Resilience tests:** Duplicate/out-of-order messages, timeouts, partial provider failure, restarts, and broker reconnects.
- **Load tests:** Concurrent calls, audio/session throughput, event bursts, dashboard fan-out, and database contention.
- **Security tests:** Tenant isolation, authorization matrix, webhook replay, prompt injection, malicious documents, export permissions, and sensitive-data leakage.

No real patient data should be used in automated tests or non-production environments.

## 15. Local Development and Environments

Use containers for reproducible local dependencies: relational database, MQTT broker, cache, object storage emulator, telemetry collector, and optional mail/SMS test sink. Use provider simulators for telephony, speech, AI, and scheduling so core development does not depend on paid services.

Environments:

- **Local:** Synthetic data and emulators.
- **Development:** Shared integration environment with synthetic data.
- **Staging:** Production-like topology and sanitized/synthetic test tenants.
- **Production:** Strict access, audited operations, protected secrets, backups, alerts, and change controls.

Configuration is validated on startup. Infrastructure and broker ACLs are versioned as code. Database migrations are forward-compatible and deployed independently from application startup.

## 16. Decisions Required Before Coding

Record each accepted decision in `docs/adr/`.

### Product and workflow

- Exact MVP call intents and which actions the AI may execute without staff approval.
- Inbound only versus inbound and outbound for MVP.
- Human handoff destinations, business-hours behavior, timeout behavior, and voicemail rules.
- Supported languages, accessibility needs, and tenant customization limits.
- Open Dental deployment/interface used by each pilot, available API capabilities, test environment, rate limits, and supported patient/appointment operations.
- Transcript/recording visibility, retention defaults, download/export policy, and whether recording is optional.
- Success metrics: containment, booking conversion, transfer completion, caller satisfaction, latency, and maximum cost per minute/call.

### Technology

- Supported .NET version and hosting platform.
- Relational database, cache, object storage, and search/vector strategy.
- MQTT broker and operating model; required persistence, clustering, ACL, and MQTT version.
- Telephony, speech-to-text, text-to-speech, AI model/provider, messaging, and identity providers.
- BFF realtime transport: SSE or WebSocket. MQTT remains internal.
- BFF deployment boundary, session approach, CSRF strategy, internal service authentication, and whether response composition occurs in-process or through private APIs.
- Open Dental synchronization strategy: polling/reconciliation interval, supported change notifications, conflict policy, timestamp mapping, and acceptable data staleness.
- Single database with tenant keys versus stronger database/schema isolation for regulated or enterprise tenants.
- Deployment model, regions, data residency, high availability, RPO/RTO, and observability vendors.

### Security and governance

- Data owner/controller/processor roles and applicable jurisdictions.
- Vendor agreements and permitted data usage/training terms.
- Authentication, MFA, roles, support access, break-glass process, and audit review.
- Consent wording and evidence for recording, messaging, outbound calls, and AI disclosure.
- Data classification, retention schedule, deletion SLA, backup retention, and legal holds.
- Incident response owner, security contact, vulnerability handling, and production access approval.

## 17. Recommended MVP Scope

Keep the first pilot deliberately narrow:

- One region and language.
- Inbound calls for a small number of tenant offices and locations.
- Office information, new-patient lead capture, appointment request/booking, confirmation, and staff transfer.
- One telephony provider, one AI/speech path, one messaging provider, and the Open Dental practice-management adapter.
- React accesses all platform capabilities through the Web BFF; SSE or WebSocket delivers sanitized live updates.
- Office dashboard for live/recent calls, summaries, requests, integration health, knowledge/configuration, and audit history.
- Human fallback for unsupported intent, low confidence, urgent/sensitive situations, and provider failures.

Defer broad outbound campaigns, payments by phone, autonomous clinical triage, many integration vendors, custom per-tenant prompt code, and premature microservice extraction.

## 18. Delivery Phases and Exit Criteria

### Phase 0 — Discovery and risk reduction

- Interview pilot offices and map call types, scripts, scheduling rules, transfers, consent, and failure cases.
- Validate vendor APIs, contracts, regional support, latency, cost, and compliance posture with short technical spikes.
- Produce context/data-flow diagrams, threat model, event catalog, glossary, and initial ADRs.
- Exit when the MVP, vendors, compliance obligations, success metrics, and high-risk integrations are understood.

### Phase 1 — Platform foundation

- Establish repository, CI, local containers, identity/tenancy, Web BFF session/security boundary, observability, database migrations, MQTT ACLs, outbox/inbox, and contract validation.
- Exit when a tenant-scoped synthetic event flows end-to-end with traces, retries, audit, and automated isolation tests.

### Phase 2 — Call skeleton

- Receive a simulated/real inbound call, manage the call state machine, stream audio, provide safe static answers, transfer, and persist completion.
- Exit when failure and reconnection scenarios are tested and operational dashboards exist.

### Phase 3 — Dental MVP tools

- Add the Open Dental adapter, patient/appointment projection and reconciliation, approved knowledge, appointment availability/requests/bookings, confirmation, and office-specific policies.
- Exit when golden conversation evaluations and integration contract tests meet agreed accuracy/safety targets.

### Phase 4 — Pilot hardening

- Complete security review, load/resilience testing, backup restore, incident drills, retention automation, support tools, and cost controls.
- Exit when pilot acceptance criteria and go-live checklist are signed off.

### Phase 5 — Generalization

- Extract the dental policy pack behind stable vertical interfaces, add a second vertical as proof, and extract services only from measured operational need.

## 19. Definition of Done

A feature is complete only when it has:

- Acceptance criteria and tenant/role authorization rules.
- Domain behavior plus versioned HTTP/event contracts where applicable.
- Idempotency, retries, timeout, and failure/compensation behavior.
- Audit, telemetry, redaction, data classification, and retention behavior.
- Unit and appropriate integration/contract/end-to-end tests.
- Accessibility and localization review for UI changes.
- Documentation, operational alert, and runbook updates when applicable.
- Rollout, rollback, migration, and feature-flag plan for risky changes.

## 20. First Planning Backlog

1. Select two or three pilot dental offices and document their top call flows.
2. Create the domain glossary and call-state diagram.
3. Inventory data collected in every flow and complete an initial privacy/threat assessment.
4. Validate the pilot offices' Open Dental deployments, API access, test-data path, supported appointment operations, identifiers, timestamps, and rate limits.
5. Evaluate telephony, AI/speech, MQTT, identity, hosting, and remaining vendors against a written scorecard.
6. Decide MVP providers, region, language, integrations, and measurable acceptance criteria.
7. Write ADRs for modular-monolith boundaries, BFF/session boundary, event delivery/outbox, tenant isolation, BFF realtime transport, Open Dental synchronization, and provider abstraction.
8. Define OpenAPI/AsyncAPI conventions and the initial call, scheduling, notification, and audit schemas.
9. Build an Open Dental simulator/contract fixtures plus telephony/AI simulators and golden conversation fixtures before connecting production services.
10. Scaffold the solution only after the above decisions have owners and target dates.

## 21. Open-Question Register Template

Track unresolved decisions in issues or a table using:

| Field | Description |
|---|---|
| Question | The exact decision required |
| Owner | Person accountable for resolving it |
| Options | Viable choices, not an unbounded discussion |
| Decision criteria | Cost, risk, latency, compliance, integration support, etc. |
| Needed by | Date or delivery phase |
| Status | Open, investigating, decided, superseded |
| ADR | Link to the final decision record |

This document should evolve as decisions are made. Stable, consequential choices belong in ADRs; implementation details belong near the relevant code and contracts.
