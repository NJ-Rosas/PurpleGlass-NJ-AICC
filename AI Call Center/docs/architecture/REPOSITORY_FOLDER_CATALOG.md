# Repository Folder Catalog

## Project root

`AI Call Center/` is the application root. All source, documentation, tests, contracts, deployment definitions, and developer scripts belong below it.

Expected root files after implementation begins:

| File | Purpose | Relationships |
|---|---|---|
| `APPLICATION_DESIGN.md` | Product scope, architecture direction, major design decisions, and pre-coding checklist | Points to detailed architecture documents and ADRs |
| `README.md` | Developer entry point: prerequisites, setup, common commands, and document links | Links to source hosts, local deployment, and architecture guide |
| `.gitignore` | Repository-wide generated-file and secret exclusions | Applies to all folders below the project root |
| `.editorconfig` | Shared C#, TypeScript, JSON, YAML, and Markdown formatting rules | Consumed by editors, formatters, analyzers, and CI |
| `global.json` | Pins the approved .NET SDK | Controls all .NET projects in `src/backend` and backend tests |
| `Directory.Build.props` | Shared compiler, nullable, analyzer, warning, and build settings | Imported automatically by descendant .NET projects |
| `Directory.Packages.props` | Central NuGet package versions | Referenced by all descendant .NET projects |
| `docker-compose.yml` | Local-only dependency topology | Starts broker, database, cache, storage emulator, and telemetry dependencies |

Do not put application source files directly in the project root.

## `docs/`

Documentation that explains decisions, operations, contracts, security, and system behavior.

| Folder | Expected contents | Relationships |
|---|---|---|
| `docs/architecture/` | Folder ownership, dependency rules, runtime flows, and system diagrams | Governs all implementation folders |
| `docs/adr/` | One Architecture Decision Record per consequential accepted decision | Links back to the affected code, contract, and design section |
| `docs/api/` | Human-oriented HTTP API conventions, examples, auth behavior, error catalog | Complements generated OpenAPI files in `contracts/openapi` |
| `docs/events/` | Event catalog, ownership, delivery semantics, ordering, retry, and examples | Complements AsyncAPI and JSON schemas in `contracts` |
| `docs/compliance/` | Data inventory, classification, consent, retention, vendor agreements, access rules | Referenced by implementation, operations, and security tests |
| `docs/runbooks/` | Operational response steps for outages, failed messages, data incidents, and recovery | Links to dashboards, alerts, deploy procedures, and owning hosts |
| `docs/prototype/` | Numbered prototype scope, roadmap, checklists, vertical slices, acceptance, demos, and backlog | Converts architecture into gated implementation phases |
| `docs/threat-model/` | Trust boundaries, assets, abuse cases, threats, and mitigations | Updated when new integrations, data flows, or externally exposed endpoints appear |

Documentation never replaces executable tests or schemas. OpenAPI, AsyncAPI, and JSON schemas remain the machine-verifiable contracts.

## `contracts/`

Transport-neutral and externally reviewable contract artifacts.

| Folder | Expected contents | Relationships |
|---|---|---|
| `contracts/openapi/` | Versioned OpenAPI documents for BFF, internal/public APIs, and provider webhooks where appropriate | Used for documentation, compatibility validation, and generated clients |
| `contracts/asyncapi/` | MQTT topic and event/command channel definitions | Must match backend event contract types and broker topic policies |
| `contracts/schemas/` | JSON schemas, examples, compatibility fixtures, and standard event-envelope schema | Used by producers, consumers, contract tests, and CI |

Contracts describe stable boundaries, not internal domain entity persistence shapes. Sensitive example values must always be synthetic.

## `src/backend/`

All production C#/.NET backend source. Its four main areas have distinct responsibilities.

### `BuildingBlocks/`

Small, carefully governed libraries that support multiple modules. These are not a dumping ground for miscellaneous helpers.

| Folder | Expected code | May be used by |
|---|---|---|
| `PurpleGlass.SharedKernel/` | Proven cross-domain primitives such as strongly typed IDs, result/error primitives, time abstraction, base domain-event marker, and data-classification types | Domain and application layers when the concept is truly universal |
| `PurpleGlass.Application.Abstractions/` | Command/query handler abstractions, transaction boundary interfaces, current tenant/user context, authorization result types, and application clock interfaces | Module Application layers and Hosts |
| `PurpleGlass.Eventing/` | Event envelope, serializer registry, outbox/inbox abstractions, MQTT transport abstraction, idempotent consumer support | Application/Infrastructure layers and workers; never domain behavior |
| `PurpleGlass.Observability/` | OpenTelemetry registration, metric names, tracing helpers, redaction-safe logging policies, health abstractions | Hosts, Infrastructure, and adapters |
| `PurpleGlass.Testing/` | Synthetic data builders, container fixtures, fake clocks, test tenant contexts, broker fixtures | Test projects only; production projects must not reference it |

Adding a building block requires evidence that more than one module needs the same stable abstraction. Business concepts remain in their owning module.

### `Modules/`

Each subfolder owns a business capability and contains `Domain`, `Application`, `Infrastructure`, `Contracts`, and `Presentation` layers. Detailed rules are in [Backend Modules and Dependencies](./BACKEND_MODULES_AND_DEPENDENCIES.md).

| Module | Owns | Collaborates primarily with |
|---|---|---|
| `Identity/` | User identity mapping, service identities, authentication state, roles where identity-owned | Tenancy, Audit, BFF |
| `Tenancy/` | Organizations, locations, memberships, tenant settings, plans, feature flags | Identity, Billing, all tenant-scoped modules |
| `CallManagement/` | Call session lifecycle, participants, call state, transfer, disposition, recording references | Conversation, Contacts, Analytics, telephony adapter |
| `Conversation/` | Conversation turns, AI configuration versions, tool execution, escalation, summary | Call Management, Knowledge, Scheduling, AI/speech adapters |
| `Scheduling/` | Appointment workflow, availability orchestration, appointment projection, Open Dental sync state | Contacts, Dental vertical, Open Dental adapter, Notifications |
| `Contacts/` | Minimal caller/contact profile, matching, language, communication preference, consent reference | Calls, Scheduling, Notifications, Audit |
| `Knowledge/` | Tenant-approved facts/documents, publication, versions, retrieval metadata | Conversation, tenant administration, storage/search adapters |
| `Notifications/` | Notification requests, templates, delivery state, opt-out enforcement | Scheduling, Contacts, messaging adapters |
| `Billing/` | Plan usage, limits, usage metering, invoice/payment references | Tenancy, Calls, AI usage, payment adapter |
| `Analytics/` | Read models and aggregates for operational/business reporting | Consumes published facts from other modules; does not control their transactions |
| `Audit/` | Append-only business/security audit records and authorized audit queries | Every sensitive module and host |
| `Verticals.Dental/` | Dental terminology, appointment-type rules, urgent-call scripts, intake policies, Open Dental mappings that are genuinely dental-specific | Conversation, Scheduling, Knowledge; implements generic vertical contracts |

### `Hosts/`

Executable ASP.NET Core or .NET worker projects. Hosts compose modules and adapters but do not own business rules.

| Host | Responsibility | Calls/uses |
|---|---|---|
| `PurpleGlass.WebBff/` | React-facing session, CSRF, UI-shaped endpoints, aggregation, sanitized SSE/WebSocket stream | Application contracts/private APIs; never Open Dental directly |
| `PurpleGlass.Api/` | Internal/platform APIs, provider webhooks, administration endpoints not specific to the React UI | Module Presentation/Application layers |
| `PurpleGlass.CallOrchestrator.Worker/` | Active-call state coordination, telephony/audio/AI workflow, tool dispatch, bounded recovery | Calls, Conversation, adapters, eventing |
| `PurpleGlass.Integrations.Worker/` | Durable external-system commands, Open Dental synchronization/reconciliation, integration retries | Scheduling and other owning modules through application ports |
| `PurpleGlass.Notifications.Worker/` | Durable SMS/email delivery requests, provider calls, delivery-state ingestion | Notifications module and messaging adapters |
| `PurpleGlass.Migrations/` | Controlled database schema migration execution | Module Infrastructure migrations; no runtime business endpoints |

Each host should contain only startup/composition, middleware, health/telemetry setup, endpoint/consumer registration, and host-specific configuration models.

### `Adapters/`

Provider- or technology-specific implementations of interfaces owned by Application layers.

| Folder | Expected code | Contract owner |
|---|---|---|
| `Adapters/Telephony/` | Telephony SDK client, webhook signature validation, call/audio mapping, transfer implementation | Call Management / Call Orchestrator |
| `Adapters/Speech/` | Speech-to-text and text-to-speech clients, streaming mapping, provider errors | Conversation / Call Orchestrator |
| `Adapters/AI/` | Model client, structured tool-call translation, token/usage mapping, provider safety settings | Conversation |
| `Adapters/PracticeManagement/OpenDental/` | Open Dental client, authentication, DTOs, patient/appointment mappings, capability checks, synchronization/reconciliation | Scheduling; dental mapping policies may come from `Verticals.Dental` |
| `Adapters/Messaging/` | SMS/email provider clients, webhook validation, delivery status mapping | Notifications |
| `Adapters/Storage/` | Object storage and approved search/index implementations | Knowledge, Calls, Conversation |
| `Adapters/Payments/` | Payment/billing provider client and webhook translation; no raw card storage | Billing |

An adapter may depend on a provider SDK and an application port. A domain or application project must never depend on an adapter implementation.

## `src/frontend/`

The React/TypeScript application. It communicates only with the Web BFF.

| Folder | Expected code |
|---|---|
| `src/frontend/src/app/` | Store creation, router, application providers, error boundaries, app shell, typed hooks |
| `src/frontend/src/features/` | Business-facing feature slices, RTK Query endpoint injection, feature components, selectors, and tests |
| `src/frontend/src/entities/` | Shared normalized frontend entity types and presentation-safe entity components |
| `src/frontend/src/pages/` | Route-level page composition; pages coordinate features but contain little business logic |
| `src/frontend/src/components/` | Reusable application components that are not domain entities or base design-system primitives |
| `src/frontend/src/services/` | BFF HTTP base query, realtime client/middleware, error translation, generated client integration |
| `src/frontend/src/hooks/` | Reusable React hooks with a clear cross-feature purpose |
| `src/frontend/src/design-system/` | Accessible visual primitives, tokens, themes, layout, and Storybook stories if adopted |
| `src/frontend/src/types/` | Truly global TypeScript declarations; feature-specific types stay with their feature |
| `src/frontend/src/test/` | Frontend test setup, factories, MSW handlers, accessibility helpers |

Feature folders:

| Feature | Expected behavior |
|---|---|
| `features/auth/` | Login/session status, authorized tenant/location selection, permission-aware UI |
| `features/calls/` | Live/recent calls, call workspace, disposition, transfer UI, sanitized transcript/summary display |
| `features/appointments/` | Appointment calendar/list, request handling, create/reschedule/cancel workflows through BFF |
| `features/contacts/` | Authorized minimal contact lookup/detail and communication preferences |
| `features/knowledge/` | Knowledge item editing, approval, version status, publication workflow |
| `features/analytics/` | Dashboard/report queries, filters, visualizations, export requests |
| `features/settings/` | Tenant/location settings, hours, routing, integration health, roles/configuration UI |

Frontend relationship details are in [Frontend and BFF Relationships](./FRONTEND_AND_BFF_RELATIONSHIPS.md).

## `tests/`

Cross-project tests and test suites. Small unit tests may also live beside a project if the selected .NET/React test layout calls for it, but each strategy must stay consistent.

| Folder | Purpose | Depends on |
|---|---|---|
| `tests/unit/` | Fast isolated domain, application policy, reducer, selector, and utility tests | Target project plus fakes; no real network |
| `tests/integration/` | Database, MQTT, cache, storage emulator, outbox/inbox, adapter sandbox integration | Production Infrastructure plus disposable dependencies |
| `tests/architecture/` | Automated assembly/reference and module-boundary rules | Compiled backend assemblies/project graph |
| `tests/contract/` | OpenAPI, AsyncAPI, JSON schema, provider webhook, generated-client compatibility | Contracts and adapters |
| `tests/end-to-end/` | User/call journeys across deployed hosts and frontend | Entire test environment with synthetic data |
| `tests/load/` | Concurrent call, event, BFF, and dashboard fan-out performance scenarios | Production-like test environment |
| `tests/security/` | Tenant isolation, authorization matrix, CSRF/webhook replay, prompt injection, leakage tests | Exposed boundaries and synthetic malicious inputs |

No test folder may contain real patient data, production secrets, or copied production recordings/transcripts.

## `deploy/`

Versioned deployment assets; application business behavior does not belong here.

| Folder | Expected contents | Relationships |
|---|---|---|
| `deploy/containers/` | Dockerfiles, container ignore files, image hardening definitions | Builds Hosts and frontend artifacts |
| `deploy/local/` | Local broker/database/cache/storage/telemetry config and seed topology | Used by `docker-compose.yml` and developer setup |
| `deploy/environments/` | Environment overlays and non-secret configuration references | Consumed by deployment automation |
| `deploy/infrastructure/` | Infrastructure-as-code modules for network, compute, database, broker, secrets, storage, monitoring | Provisions dependencies required by Hosts |

Secrets, private keys, production exports, and mutable runtime state must never be committed here.

## `scripts/`

Small repeatable developer, CI, migration, contract-generation, and verification commands. Scripts should validate inputs, fail clearly, avoid embedding secrets, and call real build/deployment tooling rather than duplicate application logic.

## `samples/`

Synthetic examples such as webhook payloads, event envelopes, conversation fixtures, Open Dental simulator data, and local demo tenant configuration. Samples are non-production, non-sensitive, documented, and validated against the schemas where possible.
