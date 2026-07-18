# Frontend and BFF Relationships

## Boundary rule

The React application communicates only with `PurpleGlass.WebBff`. It never calls internal APIs, MQTT, Open Dental, AI/speech, telephony, messaging, databases, or object storage directly.

```text
Browser / React
      │ same-origin HTTPS + secure session cookie
      │ BFF HTTP commands/queries + SSE/WebSocket
      ▼
PurpleGlass.WebBff
      │ private authenticated application contracts/APIs
      ▼
Owning backend modules
      │ application ports, outbox/events
      ▼
Workers and provider adapters
```

## Why the BFF exists

The BFF creates a browser-specific security and data-composition boundary. It owns:

- Login/logout callbacks and secure browser session handling.
- CSRF defenses, browser-facing rate limiting, security headers, and permitted origins.
- Resolving the authenticated user's allowed tenant/location/role context.
- Aggregating several backend results into a response shaped for one screen.
- Removing sensitive or unauthorized fields before data reaches the browser.
- Translating backend errors into stable UI error codes and Problem Details.
- Delivering authorized live updates through SSE or WebSocket.

The BFF does not own patient, call, appointment, notification, billing, or integration rules. It invokes the owning Application use case, which revalidates authorization and domain policy.

## Frontend dependency direction

```text
pages
  └── use features and entities to compose routes

features
  ├── use app/store infrastructure
  ├── use shared entities and design-system components
  └── call BFF through services / injected RTK Query endpoints

entities
  └── use design-system primitives; do not initiate business workflows

components and hooks
  └── shared only when genuinely cross-feature

services
  └── transport/realtime infrastructure; no React page behavior

design-system
  └── lowest visual layer; no feature or API dependency
```

Pages can compose features, but one feature should not import another feature's internal slice/components. If cross-feature behavior is necessary, expose a small public feature API or move the shared concept to `entities` only when it is truly shared.

## Redux Toolkit state ownership

### RTK Query: remote server state

RTK Query owns data fetched from the BFF:

- Calls and call details.
- Appointments and appointment requests.
- Contacts visible to the current role.
- Knowledge items and versions.
- Tenant/location settings and integration health.
- Analytics reports.

Use endpoint tags and targeted cache invalidation. Do not copy normal query results into feature slices. Normalize only highly dynamic datasets such as live calls when it provides clear value.

### Redux slices: client/workflow state

Slices own state that exists primarily in the current UI session:

- Current authorized tenant/location selection.
- Filters, sorting, selected rows, visible panels, and calendar mode.
- Multi-step form draft state that has not been accepted by the server.
- Live UI connection status and transient optimistic-operation metadata.
- Non-sensitive display preferences.

Do not persist access tokens, patient records, transcripts, recordings, or sensitive appointment details to local storage.

### Local React state

Use component state for state owned by one small component subtree: expanded sections, focus state, temporary input values, and visual toggles. Promote it only when multiple distant components require coordinated access.

## Feature-to-BFF mapping

| Frontend feature | BFF responsibility | Owning backend module(s) |
|---|---|---|
| `auth` | Session status, logout, authorized tenant/location switch | Identity, Tenancy |
| `calls` | Live/recent call view models, call detail, authorized transfer/staff actions | Call Management, Conversation, Audit |
| `appointments` | Calendar/list composition, availability, booking/reschedule/cancel commands | Scheduling, Contacts, Dental vertical |
| `contacts` | Authorized contact search/details with field filtering | Contacts, Audit |
| `knowledge` | Knowledge administration queries/commands and publication status | Knowledge, Audit |
| `analytics` | Dashboard-specific aggregation and export job requests | Analytics, Billing where cost is displayed |
| `settings` | Tenant/location/routing/integration configuration and health | Tenancy, Identity, Knowledge, Integration projections, Audit |

The BFF can combine module results but does not join their databases. Composition happens through approved application queries or read projections.

## HTTP relationships

- Browser endpoints use `/bff/v1`.
- RTK Query uses one same-origin base URL and includes the session cookie according to the selected security design.
- Mutating requests carry CSRF protection and idempotency keys where duplicate execution would be harmful.
- The BFF returns stable error codes so UI decisions do not depend on exception messages.
- Pagination, sorting, and filtering happen on the server for large datasets.
- Generated OpenAPI types may be used at the transport boundary, then mapped into feature/entity types when UI needs differ.

Do not reuse BFF response DTOs as Redux action names, domain entities, or Open Dental DTOs. Each boundary can evolve independently through explicit mapping.

## Realtime relationships

Internal services publish MQTT events. The browser does not subscribe to MQTT.

1. The BFF consumes or receives an internal sanitized event/projection.
2. The BFF checks current session, tenant, location, role, and field permissions.
3. The BFF emits a small browser event through SSE/WebSocket.
4. Realtime middleware validates the event type/version.
5. Middleware dispatches a typed Redux action, updates a normalized live entity, or invalidates RTK Query tags.
6. The UI re-renders from state; components do not manipulate a socket directly.

Browser realtime events must exclude raw audio, provider credentials, unrestricted transcripts, internal prompts, Open Dental DTOs, and fields the current role cannot view.

## Recommended feature folder contents

Each feature may contain only what it needs:

```text
features/calls/
├── api/              # Injected RTK Query endpoints and transport mapping
├── components/       # Calls-specific UI
├── model/            # Slice, selectors, entity adapter, feature types
├── pages/            # Optional calls-owned route components
├── hooks/            # Calls-specific hooks
├── test/             # Feature tests and synthetic fixtures
└── index.ts           # Deliberate public feature exports
```

Do not create every subfolder automatically. Add one when there is a file with that responsibility.

## Example: appointment reschedule

1. Appointment page reads server state using the appointments RTK Query endpoint.
2. A feature component captures a proposed time in local/draft state.
3. The feature calls the BFF reschedule mutation with appointment ID, expected version, requested time, and idempotency key.
4. The BFF resolves user/tenant/location and calls Scheduling.
5. Scheduling validates policy and delegates Open Dental work through its port/worker flow.
6. The BFF returns accepted/pending/completed state according to the chosen API design.
7. Reconciliation confirms the authoritative Open Dental record.
8. A sanitized event invalidates the appointment query or updates the projection.
9. UI displays success/conflict/follow-up state based on stable codes, not provider error text.

## Testing relationships

- Design-system components: accessibility and visual behavior tests.
- Feature model: reducers, selectors, and endpoint mapping tests.
- Feature components/pages: React Testing Library with BFF requests mocked through MSW.
- BFF: endpoint authorization, CSRF, composition, error mapping, and field-redaction tests.
- Contract: generated client/OpenAPI compatibility tests.
- End-to-end: browser through BFF to synthetic backend/provider simulators.

