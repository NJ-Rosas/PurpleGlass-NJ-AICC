# Runtime and Integration Flows

## Interaction types

| Type | Meaning | Use when |
|---|---|---|
| HTTP query | Request current data and wait for a response | BFF screen composition or bounded internal lookup |
| HTTP command | Request a user-visible action | BFF mutation or provider webhook ingestion |
| MQTT command | Request asynchronous worker behavior | Internal durable/real-time coordination with explicit ownership |
| MQTT event | Announce an immutable fact | Other modules/workers need to react independently |
| Database transaction | Atomically change one owning module | Business state must be consistent locally |
| Outbox/inbox | Bridge database state and messaging safely | A committed state change must eventually publish/process once effectively |
| SSE/WebSocket event | Send a sanitized live UI projection | Dashboard needs immediate authorized updates |

MQTT is transport, not ownership and not the system of record.

## Standard event relationship

```text
Owning Application handler
   ├── validates authorization and domain policy
   ├── changes owning aggregate/projection
   ├── stores business data + OutboxMessage in one transaction
   ▼
Outbox publisher
   └── publishes versioned envelope to tenant-scoped MQTT topic
          ▼
Consumer
   ├── checks InboxMessage/messageId
   ├── validates schema/version/tenant
   ├── invokes its own Application use case
   └── records completion or retry/dead-letter state
```

Consumers assume duplicates, stale messages, out-of-order delivery, timeouts, and restarts.

## Inbound call flow

1. Telephony webhook reaches `PurpleGlass.Api` or a narrowly exposed telephony endpoint.
2. Telephony adapter validates the raw-body signature, replay window, and provider event ID.
3. Call Management creates/finds an idempotent `CallSession` and commits `CallReceived` through its outbox.
4. Call Orchestrator receives the work, loads tenant/location call configuration through approved Application queries, and starts the active session.
5. Telephony, Speech, and AI adapters handle provider protocols. They return provider-neutral results to the orchestrator.
6. Conversation owns turns, prompt/config version, tool requests, safety/escalation, and summary workflow.
7. A requested tool is routed to the owning module. The model cannot execute database/provider actions directly.
8. Call Management controls call-state transitions and telephony transfer/end requests.
9. On completion, Call Management and Conversation store their respective authoritative state and publish facts.
10. Analytics, Billing, Audit, Notifications, and the BFF live projection consume only the facts they need.

Critical relationship: Call Management owns call state; Conversation owns conversational reasoning state. Neither should silently update the other's tables.

## Appointment and Open Dental flow

### Read/display

1. Integrations Worker synchronizes allowed Open Dental patient appointment records through the Open Dental adapter.
2. Scheduling maps provider DTOs into its local authorized projection and stores external ID, source timestamps, office time zone, sync version, and reconciliation metadata.
3. BFF asks Scheduling for an appointment view; it does not query Open Dental.
4. Scheduling returns data filtered for tenant/location/role and freshness metadata where useful.
5. React receives a UI-specific model through RTK Query.

### Create/reschedule/cancel

1. Caller tool or BFF submits a typed Scheduling command with tenant context, expected version, and idempotency key.
2. Scheduling validates identity/authorization, office policy, Dental vertical rules, and current projection.
3. Scheduling records pending external work and publishes a durable command after commit.
4. Integrations Worker calls the Open Dental adapter with the tenant-scoped connection.
5. Adapter translates the command, calls Open Dental, and maps the response/error without leaking provider DTOs.
6. Worker re-reads/reconciles the affected appointment when required to confirm authoritative state.
7. Scheduling accepts the result only if it is current, updates its projection, and publishes `AppointmentBooked`, `AppointmentRescheduled`, `AppointmentCanceled`, or a failure/conflict fact.
8. Notifications may request confirmation; Analytics updates read models; BFF sends a sanitized update.

Open Dental is authoritative for synchronized patient appointment details and source timestamps. PurpleGlass is authoritative for call/conversation history, tool execution, pending workflow, integration attempts, idempotency, and audit records.

If a provider request times out after a possible write, do not immediately repeat it. Mark the result unknown and reconcile using identifiers/idempotency evidence before deciding whether another mutation is safe.

## Knowledge-answer flow

1. Authorized staff create/edit a Knowledge item through React and BFF.
2. Knowledge Application validates scope, classification, effective dates, and approval workflow.
3. Published content is indexed through an Application port implemented by Storage/search infrastructure.
4. Conversation requests approved knowledge using tenant/location and effective-time context.
5. Retrieved text is treated as untrusted data, not executable prompt instructions.
6. Conversation records source/version references used for the answer.
7. Raw documents and sensitive internal metadata are not emitted to browser realtime channels or MQTT payloads unnecessarily.

## Notification flow

1. An owning module publishes a fact such as `AppointmentBooked`; it does not send SMS itself.
2. Notifications evaluates template, channel, consent/opt-out, tenant policy, and duplicate rules.
3. Notifications commits `NotificationRequested` plus outbox work.
4. Notifications Worker invokes the Messaging adapter.
5. Provider response and delivery webhooks update Notifications through idempotent handlers.
6. Notifications publishes delivery facts for UI/Analytics/Audit consumers.

Contacts owns communication preferences/consent evidence; Notifications owns whether and how a requested delivery proceeds under those rules.

## BFF live-dashboard flow

1. Internal modules publish operational facts through MQTT/outbox.
2. A BFF projection consumer maps them to tenant/location-scoped UI events.
3. The BFF authorizes the active session and removes fields not permitted for the role.
4. SSE/WebSocket delivers a small versioned event.
5. Frontend realtime middleware dispatches an action or invalidates RTK Query tags.
6. Components render Redux/RTK Query state; they never consume broker messages directly.

## Audit relationship

Audit is required for sensitive reads, writes, exports, configuration changes, tool executions, provider mutations, support access, and authorization failures of interest.

- The module performing an action supplies actor, tenant, action, resource, outcome, reason/context, and correlation identifiers.
- Audit stores append-only records and controls audit-query authorization.
- Audit payloads contain enough evidence for review without copying full transcripts, recordings, credentials, or raw provider responses.
- Observability logs help operate the system; Audit records prove governed actions. One does not replace the other.

## Billing and usage relationship

- Call Orchestrator and provider adapters report measured usage through versioned facts.
- Billing owns plan limits, metering rules, and invoice/payment references.
- Tenancy owns tenant/plan assignment and feature availability inputs.
- Analytics may display cost/usage projections but does not create invoices.
- Provider usage discrepancies are reconciled rather than overwriting immutable usage records.

## Failure ownership

| Failure | Immediate owner | Required behavior |
|---|---|---|
| Invalid telephony webhook | Telephony boundary/API | Reject, audit/metric as appropriate, never create call |
| Speech/AI timeout | Call Orchestrator | Bounded fallback, caller-safe wording, transfer/end policy |
| Open Dental unavailable | Integrations Worker + Scheduling | Retry safely, expose stale/pending state, reconcile before duplicate mutation |
| MQTT unavailable | Eventing/host | Keep outbox durable, reconnect with limits, alert on outbox age |
| Poison message | Consumer owner | Bounded retries, dead-letter, operator context without sensitive leakage |
| BFF realtime disconnect | BFF/frontend | Reconnect with backoff; re-query authoritative projection after reconnect |
| Notification provider failure | Notifications | Respect retry/expiry/consent, publish failure, create staff follow-up if policy requires |

## Correlation and identifiers

Every cross-boundary operation carries:

- `messageId` for message deduplication.
- `correlationId` for the complete caller/user workflow.
- `causationId` for the immediate triggering message/action.
- `traceId` for distributed tracing.
- `tenantId` and, when applicable, `locationId`.
- Aggregate ID such as `callId` or PurpleGlass appointment identifier.
- External/provider ID only within authorized contracts and never in MQTT topic names.

Identifiers do not grant authorization. Every boundary resolves and verifies access independently.

## Relationship checklist for a new workflow

Document the following before implementation:

1. Owning module and authoritative record.
2. Initiator and authentication/authorization context.
3. Command/query/event contract and version.
4. Transaction boundary and outbox behavior.
5. Idempotency key and duplicate behavior.
6. Ordering/version/conflict policy.
7. Provider adapter and timeout/retry/reconciliation behavior.
8. Sensitive fields, redaction, audit, consent, and retention.
9. UI projection and realtime behavior.
10. Unit, integration, contract, security, resilience, and end-to-end coverage.

