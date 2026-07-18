# Prototype 1: First Vertical Slice

## Slice name

**View and update the current dental-office location display name.**

This is intentionally simple business behavior. Its purpose is to exercise all architectural boundaries without introducing patient data, appointments, telephony, or AI.

## User story

As an authorized synthetic office administrator, I can view my current tenant and location and update the location display name so that the dashboard demonstrates tenant-scoped data, persistence, events, auditing, and realtime state refresh.

## Domain responsibilities

### Tenant

Owns:

- `TenantId`.
- Tenant status.
- Legal/display name rules if included in the prototype.
- Collection or association of authorized locations.

Minimum invariants:

- Identifier is never empty.
- Display value is normalized and length-bounded.
- Inactive tenant cannot perform mutable prototype operations.

### Location

Owns:

- `LocationId` and `TenantId` association.
- Display name.
- IANA time-zone identifier.
- Status and concurrency version.

Minimum invariants:

- Location belongs to exactly one tenant.
- Display name is required after trimming and has an agreed maximum length.
- Time-zone identifier is valid according to the chosen server policy.
- State changes produce a domain fact only when the effective value changes.

## Application use cases

### `GetCurrentTenantSummary`

Input:

- Current actor/session context supplied by the BFF boundary.
- No trusted tenant ID from the browser.

Behavior:

1. Resolve authorized tenant and selected location from server context.
2. Query the Tenancy-owned read model/repository.
3. Enforce active membership/tenant/location status.
4. Return an Application result mapped to a BFF-specific response.

Output fields should be minimal: opaque tenant/location IDs if required by UI, display names, time-zone identifier, status, and concurrency version.

### `UpdateLocationDisplayName`

Input:

- Location identifier.
- Proposed display name.
- Expected concurrency version.
- Request idempotency key.
- Current actor/tenant context supplied by the server.

Behavior:

1. Validate boundary shape.
2. Verify tenant/location authorization.
3. Load the aggregate within the tenant scope.
4. Apply the Domain operation.
5. If changed, store Location, Audit record/request, and Outbox message consistently according to the selected transaction design.
6. Return accepted authoritative state and new version.

Stable failures:

- Unauthenticated.
- Forbidden tenant/location.
- Location not found within authorized scope.
- Invalid display name.
- Tenant/location inactive.
- Concurrency conflict.
- Duplicate idempotency key with incompatible input.
- Dependency unavailable.

## Persistence relationships

Suggested conceptual records:

- `tenancy.tenants`
- `tenancy.locations`
- `tenancy.outbox_messages` or a shared eventing outbox with explicit owner
- `tenancy.idempotency_records`
- `audit.audit_records`
- consumer `inbox_messages`

Exact table design belongs in implementation and migrations, but no module may update Tenancy tables except Tenancy Infrastructure/Application behavior.

## Event contract

Integration event: `LocationDisplayNameChanged`.

Payload contains only:

- Opaque `locationId`.
- Previous and current display values only if the data-classification review permits both; otherwise current value or a change marker.
- New concurrency/version value.
- Change timestamp.

The standard envelope supplies tenant, message, schema, correlation, causation, trace, and producer metadata. Topic names use opaque tenant/location IDs and never the display name.

## Consumer

The first consumer should have one demonstrable purpose, such as maintaining a sanitized dashboard projection or recording idempotent receipt. It must:

- Validate supported event/schema version.
- Verify required tenant context.
- Check inbox deduplication before side effects.
- Reject malformed or incompatible messages.
- Store completion atomically with its own projection change where applicable.
- Produce observable retry/dead-letter information without logging the full payload.

## BFF relationship

Suggested browser endpoints:

- `GET /bff/v1/session`
- `GET /bff/v1/tenant-summary`
- `PUT /bff/v1/locations/{locationId}/display-name`
- `GET /bff/v1/realtime` for SSE, if SSE is selected

The BFF:

- Creates/resolves the synthetic development session.
- Ignores or verifies any client-supplied tenant context.
- Calls Tenancy through an approved Application boundary.
- Maps stable errors to Problem Details.
- Maps internal contracts into screen-specific response models.
- Filters and emits only the permitted realtime event.

## Frontend relationship

Suggested feature ownership:

- `features/auth`: synthetic session status and authorized context display.
- `features/settings`: tenant summary and location display-name edit workflow.
- `services`: RTK Query base API and realtime middleware/client.
- `app`: store, router, providers, and app shell.
- `design-system`: accessible form, button, status, and error primitives.

State rules:

- Tenant summary is RTK Query server state.
- Edit input is local/draft state until submitted.
- Connection status may be a small Redux slice.
- Successful realtime event invalidates the tenant-summary tag or updates a normalized projection.
- Sensitive or token data is not persisted.

## Required tests

### Domain

- Valid rename changes state/version and produces one fact.
- Whitespace normalization behaves consistently.
- Empty/too-long value fails.
- Same effective name produces no change event.
- Inactive tenant/location fails.

### Application

- Authorized command succeeds.
- Cross-tenant command is forbidden even when the location ID exists.
- Stale expected version returns conflict.
- Duplicate idempotent request does not repeat side effects.
- Successful transaction includes outbox and audit intent.

### Integration/eventing

- Migration creates expected schema.
- Data persists and query returns only tenant-scoped record.
- Outbox is not visible as publishable before commit.
- Broker outage preserves pending message.
- Duplicate MQTT delivery yields one effective consumer result.

### BFF/frontend

- Unauthorized session is handled.
- BFF response omits internal fields.
- Page displays loading, success, validation, conflict, and dependency-failure states.
- Browser request targets BFF only.
- Realtime update refreshes visible state.
- Keyboard and accessible-label behavior passes.

## Slice exit gate

- All required tests pass.
- Primary success scenario works from the UI.
- Cross-tenant and duplicate-event demonstrations fail safely.
- Trace and audit evidence are visible.
- No real patient/office data or secrets exist.

