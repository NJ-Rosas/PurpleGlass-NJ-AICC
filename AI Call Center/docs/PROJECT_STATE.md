# PurpleGlass project state

Last updated: July 26, 2026

## Executive summary

PurpleGlass is a working cloud-development prototype for a multi-tenant dental-office call center. The React dashboard, ASP.NET Core Web BFF, integrations worker, PostgreSQL persistence, MQTT event delivery, durable telephony operations, Twilio carrier adapter, signed webhooks, realtime media transport, SSE dashboard updates, security boundaries, observability, and local-development tooling are implemented.

The cloud environment is live on Render at `https://purpleglass-web.onrender.com`. Real Twilio telephony is enabled through manual Render environment overrides, while real AI, speech-to-text, and text-to-speech remain disabled. San Juan Prototype Office has an active outbound Twilio-number assignment stored through the authenticated PurpleGlass administration API. The number and all provider credentials are intentionally excluded from source control.

No automated validation has placed a real telephone call. The next acceptance milestone is a supervised outbound call to a recipient verified on the Twilio trial account.

## Current cloud-development deployment

| Component | Current implementation | Development deployment |
| --- | --- | --- |
| Browser application | React and TypeScript dashboard | Served by `purpleglass-web` |
| Web boundary | ASP.NET Core Web BFF | Render Free Web Service |
| Integration processing | Outbox publisher and telephony dispatcher | `purpleglass-integrations-worker`, wrapped as a Render Free Web Service |
| Durable store | PostgreSQL with EF Core migrations | Render Free PostgreSQL |
| Realtime events | MQTT plus browser SSE projection | HiveMQ Cloud Serverless over authenticated TLS |
| Telephony | Provider-neutral boundary with Twilio adapter | Enabled manually in both Render services |
| AI and speech | Provider-neutral realtime pipeline with fake, disabled, and OpenAI adapters | Real AI and speech disabled |
| Local infrastructure | PostgreSQL, Mosquitto, and Valkey | Docker Compose and one-click PowerShell launcher |

The repository's `render.yaml` remains safe by default: real telephony, AI, and speech are disabled there. Live Twilio enablement is an intentional environment override in the Render Dashboard, not a committed default.

## Working application capabilities

- Development administrator login with server-owned identities and permissions.
- Cookie-authenticated BFF session, antiforgery protection, tenant/location authorization, security headers, HTTPS enforcement, and rate limits.
- Tenant and location summary for San Juan Prototype Office.
- Durable inbound and outbound call lifecycle with idempotent request handling.
- Location-scoped telephony-number administration through `GET` and `PUT /bff/v1/telephony/numbers`.
- Transactional outbox, inbox deduplication, leasing, retry/dead-letter handling, and operator recovery workflow.
- Integrations worker dispatch of durable start and hangup operations.
- Twilio outbound-call submission using the persisted location number as caller ID.
- Signed Twilio inbound, outbound-answer, and status webhooks.
- Canonical external-URL signature verification behind Render's proxy.
- Twilio Media Stream WebSocket transport at `/telephony/twilio/media`.
- Provider-neutral realtime conversation pipeline with bounded audio and message handling.
- Call lifecycle and voice-state updates through MQTT and same-origin SSE.
- Structured observability with safe metrics, tracing, correlation, and sanitized operational errors.
- Database migration host used automatically by the Render web container before BFF startup.
- Repeatable local startup and shutdown scripts with safe synthetic defaults.

## Telephony configuration state

The live Render services currently select the Twilio provider and enable real telephony. Both services require the existing Twilio Account SID, Auth Token, and canonical public base URL because the worker submits provider requests while the BFF verifies callbacks and media connections.

PurpleGlass does not consume a `Telephony__Twilio__FromNumber` setting. The outbound caller ID is resolved from the active `call_management.telephony_numbers` assignment for the authenticated tenant and requested location. San Juan Prototype Office currently has one active, outbound-enabled Twilio assignment created through the protected application API. Inbound routing remains disabled.

The implemented public carrier routes are:

- `POST /telephony/twilio/inbound`
- `POST /telephony/twilio/answer?operationId=<operation-id>`
- `POST /telephony/twilio/status?operationId=<operation-id>`
- `WSS /telephony/twilio/media`

The answer and status URLs are generated per durable operation. The inbound webhook should not be configured in Twilio until inbound calling is intentionally enabled and tested.

## Persistence and worker status

The dedicated migration host applies the Tenancy, Eventing, CallManagement, and Conversation migrations in dependency order. A clean PostgreSQL reproduction confirmed that the current migrations create both `eventing.outbox_messages` and `call_management.telephony_operations`. The schema defect observed in an older worker deployment is not present in the current migration path.

The BFF owns migrations in the free Render topology. The integrations worker never creates or modifies schema; it consumes durable outbox and telephony-operation records after the BFF migration step succeeds.

## Latest validation baseline

The cloud-foundation and Twilio-readiness work passed:

- Release build of the complete backend solution: zero warnings and zero errors.
- Unit tests: 128 passed.
- Architecture tests: 14 passed.
- Integration tests: 62 passed.
- Frontend tests: 20 passed across four test files.
- Frontend production build.
- Render worker Docker image build.
- Render web Docker image build, including the frontend and migration host.
- Fresh PostgreSQL migration and required-table verification.
- Git whitespace validation and repository secret-pattern scan.
- Live BFF readiness check returned HTTP 200 after Twilio configuration.
- Protected API read confirmed the active San Juan outbound Twilio assignment.

Test fixtures use synthetic SIDs, tokens, and phone numbers. Automated tests do not contact Twilio or place calls.

## Important limitations

- This is a development prototype, not a production or HIPAA-ready deployment.
- Render Free Web Services sleep when idle and may cold-start too slowly for carrier webhooks.
- The free worker wrapper needs an explicit HTTP wake-up before a call experiment.
- Render Free PostgreSQL has limited storage, no backups, and a time-limited lifecycle.
- Web BFF Data Protection keys are ephemeral, so a restart can invalidate browser sessions and CSRF cookies.
- Active realtime voice sessions are process-local and require exactly one BFF instance.
- HiveMQ Serverless and the free Render topology provide no production availability guarantees.
- OpenAI AI, STT, and TTS are disabled; a connected media stream does not yet provide an AI conversation.
- Inbound Twilio routing is not configured or enabled.
- The first real outbound call and end-to-end carrier callback lifecycle remain to be validated manually.

## Next recommended milestone

Run one controlled Twilio trial outbound call:

1. Verify the destination number in the Twilio Console and keep it in E.164 format.
2. Wake both Render services and wait for the BFF `/health/ready` and worker `/health/live` endpoints.
3. Sign in as the PurpleGlass development administrator.
4. Confirm the telephony provider reports `Twilio`, enabled, and configured.
5. Enter only the verified trial destination and press **Call** once.
6. Confirm Twilio accepts the request and the test handset rings.
7. Confirm initiated, ringing, answered, and completed callbacks reach PurpleGlass.
8. Confirm the durable call state and dashboard/SSE projection update correctly.
9. Inspect only sanitized PurpleGlass logs and the Twilio Call log if the call fails.

Keep real AI and speech disabled for this milestone. Do not add OpenAI until the carrier-only path has been proven.

## Operator references

- [Render cloud-development runbook](./runbooks/RENDER_CLOUD_DEVELOPMENT.md)
- [Local prototype runbook](./runbooks/LOCAL_PROTOTYPE.md)
- [Telephony architecture and operations](./TELEPHONY.md)
- [Realtime voice pipeline](./architecture/REALTIME_VOICE_PIPELINE.md)
- [Observability](./OBSERVABILITY.md)
- [Dead-letter recovery](./runbooks/DEAD_LETTER_RECOVERY.md)
- [Architecture decision records](./adr/)

Never commit or publish provider credentials, authorization headers, signatures, real phone numbers, call audio, transcripts, patient information, or production connection strings.
