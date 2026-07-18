# Backend Modules and Dependencies

## Purpose

The backend is a modular C#/.NET system. A module owns a business capability and controls changes to its authoritative data. Layers separate business meaning from orchestration, transport, persistence, and provider technology.

The initial deployment can package several modules in one process. Sharing a process does not permit bypassing module boundaries.

## Standard module layers

Every module uses the following conceptual dependency direction:

```text
Presentation ───────► Application ───────► Domain
                          ▲                   ▲
Infrastructure ───────────┘───────────────────┘

Contracts is the deliberately exposed boundary used by approved consumers.
Hosts compose Presentation, Application, Infrastructure, adapters, and Contracts.
```

Arrows mean “may reference.” Domain never points outward.

### `Domain/`

Contains business meaning and invariant enforcement.

Expected file types:

- Aggregates and entities with controlled state transitions.
- Value objects such as appointment time windows, phone-number values, call outcomes, and strongly typed identifiers.
- Domain policies and specifications that answer business questions without infrastructure.
- Domain events representing facts produced by an aggregate.
- Domain exceptions only when a Result/error model is insufficient.
- Repository interfaces only when they express a domain collection; otherwise persistence ports belong in Application.

Domain code may use the C# base class library and approved SharedKernel primitives. It must not reference ASP.NET Core, Entity Framework Core, MQTT, Open Dental/provider SDKs, HTTP DTOs, database models, logging providers, or another module's Infrastructure.

### `Application/`

Contains use cases and coordination around the domain.

Expected file types:

- Commands, queries, handlers, validators, and authorization requirements.
- Input/output models internal to application use cases.
- Ports/interfaces required from databases, external systems, time, current actor, AI tools, or publishers.
- Transaction coordination and idempotency policies.
- Mapping between Domain objects and module Contracts.
- Application events or outbox requests after successful transactions.

An Application handler loads state through a port, asks Domain objects/policies to make business decisions, persists through a port, and records/publishes resulting facts. It does not contain provider-specific retry codes or construct HTTP/MQTT responses.

### `Infrastructure/`

Implements technical details required by its module.

Expected file types:

- Entity Framework Core `DbContext`, entity configurations, migrations, and repositories.
- Outbox/inbox persistence and module-specific consumer checkpoints.
- Cache implementations and serialization details.
- Search/read-model implementations.
- Adapter registration and infrastructure configuration validation.
- Integration-event consumers that translate a published contract into an Application command.

Infrastructure may reference Application ports and Domain types. It cannot make a business decision that belongs in Domain/Application, expose EF entities as contracts, or reach into another module's tables.

### `Contracts/`

Contains the smallest stable surface intentionally shared outside the module.

Expected file types:

- Versioned request/response DTOs for approved internal/public boundaries.
- Integration events and commands carried between processes/modules.
- Stable enums/error codes where compatibility is managed.
- Contract-level identifiers and schema metadata.

Contracts contain data shapes, not behavior, database annotations, provider SDK types, or secrets. Changing a contract requires compatibility review and corresponding OpenAPI/AsyncAPI/schema updates.

### `Presentation/`

Adapts incoming transport requests to Application use cases.

Expected file types:

- ASP.NET endpoint definitions, request binding, response mapping, filters, and authorization declarations.
- MQTT/event consumer registration when the owning module exposes a transport handler.
- Provider webhook endpoints when routing them through the main API host is appropriate.
- Problem Details/error mapping at the module boundary.

Presentation validates shape, authentication, and basic boundary rules, then invokes Application. It must not query EF directly, contain domain policies, call Open Dental/provider SDKs, or publish an event before the transaction commits.

## Reference matrix

| From | Domain | Application | Contracts | Infrastructure | Presentation | Adapter | Host |
|---|---:|---:|---:|---:|---:|---:|---:|
| Domain | Same module only | No | No | No | No | No | No |
| Application | Yes | Same module | Approved foreign Contracts only | No | No | Port abstraction only | No |
| Contracts | No | No | Same/shared contract primitives | No | No | No | No |
| Infrastructure | Yes | Yes | Approved contracts | Same module | No | Approved implementations | No |
| Presentation | Indirectly | Yes | Yes | Registration only | Same module | No direct provider calls | No |
| Adapter | Only necessary value types | Owning port | Provider-neutral contract | Technical support only | No | Same adapter | No |
| Host | Composition only | Yes | Yes | Yes | Yes | Yes | Same host only |

Architecture tests should enforce the reference rules at project/assembly level.

## Module-to-module communication

Choose the interaction based on consistency and coupling needs:

1. **Published integration event:** Default when another module only needs to react to a completed fact. Example: Analytics consumes `CallCompleted`.
2. **Application contract/query:** Allowed for a synchronous fact required to complete a user operation. Example: Scheduling asks Contacts for an authorized contact match through a narrow contract.
3. **Command to owning module:** Used when one capability asks another to perform behavior. The owning module still validates and controls its transaction.
4. **Shared database access:** Prohibited. A module never queries or updates another module's tables directly.
5. **Referencing another module's Domain assembly:** Prohibited by default. Share a contract or duplicate a small representation with explicit mapping.

Do not create circular synchronous dependencies. Break cycles with events, orchestration in an owning Application workflow, or a clarified domain boundary.

## Ownership examples

### Appointment booking

- `Scheduling.Domain` owns appointment-request and synchronization invariants.
- `Scheduling.Application` owns `BookAppointment` orchestration and defines an Open Dental port.
- `OpenDental` adapter implements that port and translates external DTOs.
- `Scheduling.Infrastructure` stores the local appointment projection and idempotency/reconciliation record.
- `Scheduling.Contracts` publishes `AppointmentBooked` after authoritative confirmation.
- `Notifications` consumes the fact and decides whether an authorized confirmation should be sent.
- `Analytics` consumes the fact for reporting.
- Neither Notifications nor Analytics changes the appointment.

### AI tool execution

- `Conversation.Application` owns the allow-listed tool registry and tool-execution audit workflow.
- A conversation tool requesting appointment availability sends a typed request to Scheduling.
- `Scheduling` applies tenant, dental, availability, identity, and Open Dental rules.
- The AI adapter receives only the provider-neutral tool result returned by Conversation.
- The model does not call Open Dental, databases, or MQTT directly.

### Human call transfer

- `CallManagement.Domain` validates whether the call can transition to transfer-requested/transferred.
- `CallManagement.Application` resolves the tenant routing policy and requests telephony transfer through a port.
- The Telephony adapter performs the provider action.
- `CallManagement` publishes the resulting fact.
- Conversation stops or changes behavior based on the call-state event.

## Host composition relationships

Hosts select which module Presentation and Infrastructure registrations run in each process.

- The BFF references UI-facing application contracts/clients and BFF composition code. It never references provider adapters.
- The API host exposes module endpoints and provider webhooks. It resolves module handlers rather than manipulating databases.
- The Call Orchestrator composes Call Management, Conversation, eventing, telephony, speech, and AI capabilities.
- The Integrations Worker composes durable integration consumers and approved provider adapters, including Open Dental.
- The Notifications Worker composes Notifications plus messaging adapters.
- The Migrations host discovers approved module migrations and applies them in a controlled order.

A host is replaceable. Business behavior must remain testable without starting ASP.NET or a worker process.

## Data and transaction boundaries

- Each module owns its tables/schema even if all modules initially share one database server.
- A transaction updates only one module's authoritative write model.
- Cross-module work uses an outbox event or an explicit orchestrated workflow with compensating/reconciliation behavior.
- Consumers record `messageId` in an inbox before producing repeatable side effects.
- Read models may combine published facts from multiple modules but cannot become an unofficial write path.
- All tenant-owned persistence includes `TenantId`; application authorization cannot rely on a query filter alone.

## Naming guidance

Use names that reveal responsibility:

- Commands use imperative language: `BookAppointment`, `RequestHumanTransfer`.
- Events use past tense: `AppointmentBooked`, `HumanTransferRequested`.
- Queries describe the result: `GetCallDetails`, `SearchAvailability`.
- Handlers end in `Handler`; ports describe capability rather than provider: `IPracticeManagementSystem`, not `IOpenDentalService` in the Application layer.
- Provider implementations reveal technology: `OpenDentalPracticeManagementSystem` in the adapter.
- Avoid generic files named `Helper`, `Manager`, `Utils`, or `Common` without one precise responsibility.

## When to extract a service

A module becomes a separately deployed service only when evidence shows a need for independent scale, availability, security isolation, ownership, deployment frequency, or technology. Extraction preserves Contracts and replaces in-process calls with transport; it must not redesign ownership casually.

