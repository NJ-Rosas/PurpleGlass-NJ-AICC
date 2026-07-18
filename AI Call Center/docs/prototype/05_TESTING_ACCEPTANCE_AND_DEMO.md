# Prototype 1: Testing, Acceptance, and Demo

## Test pyramid

### Fast tests on every change

- C# domain/application unit tests.
- Architecture dependency tests.
- TypeScript reducer, selector, mapping, and component tests.
- Formatting, compilation, linting, and schema validation.

### Integration tests

- PostgreSQL repositories, migrations, concurrency, tenant filters, outbox/inbox.
- MQTT publish/consume, QoS behavior, duplicate handling, reconnect, and invalid schema.
- BFF endpoint authorization, composition, CSRF, error mapping, and redaction.
- Realtime connection authorization and reconnect behavior.

### End-to-end tests

- Load dashboard and display synthetic tenant/location.
- Rename location and observe realtime refresh.
- Submit stale version and display conflict.
- Attempt cross-tenant access and receive no protected information.

## Mandatory negative scenarios

| Scenario | Expected result |
|---|---|
| Missing/invalid development session | `401` and signed-out UI state |
| Actor not permitted for tenant/location | `403`; no existence/data disclosure |
| Unknown location inside permitted tenant | Stable not-found response |
| Empty or excessive display name | Validation response; no transaction/event |
| Stale concurrency version | Conflict response with safe refresh guidance |
| Duplicate idempotency key, same input | Original result or safe repeat response; no duplicate effect |
| Duplicate idempotency key, different input | Conflict/rejection |
| PostgreSQL unavailable | Readiness fails; safe BFF dependency error |
| MQTT unavailable after DB commit | Business change remains committed; outbox stays pending and retries |
| Duplicate MQTT message | One effective consumer/projection change |
| Unsupported schema version | Message rejected/dead-lettered with safe diagnostics |
| Realtime disconnect | UI indicates/retries connection and re-queries after reconnect |
| Event for another tenant | Never delivered to current browser session |

## Acceptance environment reset

The final acceptance run begins from a known synthetic environment:

1. Stop prototype processes.
2. Use the explicitly documented project-scoped reset command.
3. Start Compose dependencies.
4. Apply migrations through the Migrations host/command.
5. Seed the known synthetic tenant/location.
6. Start backend hosts and frontend.
7. Confirm health/readiness before opening the dashboard.

Reset instructions must never use an unresolved variable, home directory, workspace root, or broad recursive deletion target.

## Final acceptance checklist

### Build and startup

- [ ] Clean backend restore/build succeeds.
- [ ] Clean frontend install/build succeeds from lockfile.
- [ ] Automated tests pass.
- [ ] Compose services are healthy.
- [ ] Migrations and synthetic seed complete deterministically.
- [ ] API, BFF, worker, and frontend start using documented commands.

### Functional flow

- [ ] Dashboard loads through the BFF.
- [ ] Correct synthetic tenant/location is displayed.
- [ ] Valid display-name update succeeds.
- [ ] PostgreSQL contains the new authoritative value.
- [ ] Outbox publishes the event.
- [ ] Consumer processes it exactly effectively once.
- [ ] Realtime update refreshes the UI without manual reload.
- [ ] Audit record identifies synthetic actor, action, target, outcome, and correlation.

### Safety and isolation

- [ ] Cross-tenant query and update tests pass.
- [ ] Browser has no MQTT credentials or provider/internal API access.
- [ ] Topics contain only opaque identifiers.
- [ ] Logs/traces contain no credentials or unnecessary payload data.
- [ ] Repository scan finds no secrets or real patient data.
- [ ] Development identity mechanism cannot run under production configuration.

### Resilience and visibility

- [ ] MQTT interruption/recovery proves outbox durability.
- [ ] Duplicate delivery proves inbox idempotency.
- [ ] Stale update proves optimistic concurrency.
- [ ] Realtime reconnect restores authoritative UI state.
- [ ] One correlation ID traces the complete workflow.
- [ ] Health/readiness and troubleshooting documentation are accurate.

## Demo script

### Part 1 — Architecture boundary

1. Show the repository architecture documents and project graph.
2. Start local dependencies and show health.
3. Start backend and frontend through documented commands.
4. Open browser network tools and demonstrate requests target only the BFF.

### Part 2 — Primary workflow

1. Load the dashboard.
2. Show the synthetic tenant and dental-office location.
3. Change the location display name.
4. Submit once and observe success.
5. Show the realtime UI refresh.
6. Follow the correlation ID through BFF, Application, database/outbox, MQTT, consumer, and BFF realtime logs/traces.
7. Show the safe audit record.

### Part 3 — Failure guarantees

1. Demonstrate a stale concurrency update and safe conflict UI.
2. Demonstrate that another tenant's location cannot be read or updated.
3. Pause/stop Mosquitto, make a valid update, and show the pending outbox.
4. Restart Mosquitto and show eventual publish/processing.
5. Replay the event and show no duplicated effect.

### Part 4 — Limitations and next step

State explicitly that the prototype contains no real authentication, patient data, Open Dental, telephony, speech, or AI. The proposed next milestone is the Call Management state machine with a simulated telephony adapter.

## Evidence to retain

- CI run URL/identifier.
- Test summary and dependency/secret scan results.
- Exact tool and image versions.
- Acceptance date and operator.
- Screenshots or short recording using synthetic data only.
- Known limitations and accepted deviations linked to ADRs/issues.

