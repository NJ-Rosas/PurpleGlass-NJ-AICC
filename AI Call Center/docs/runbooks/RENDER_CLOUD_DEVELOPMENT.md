# Render cloud development

> **Development / prototype only.** This topology targets a $0 recurring infrastructure baseline for initial cloud and live-provider experiments. It is not production-ready or HIPAA-ready.

## Free-development topology

| Component | Location and plan | Baseline cost | Important limitation |
| --- | --- | ---: | --- |
| React UI and Web BFF | Render Web Service, Free | $0/month | Sleeps after 15 idle minutes; ephemeral filesystem; one instance |
| Integrations Worker | Render Web Service, Free | $0/month | Development-only web wrapper; sleeps without inbound traffic |
| PostgreSQL | Render Postgres, Free | $0/month | 1 GB, no backups, one per workspace, expires after 30 days |
| MQTT | HiveMQ Cloud Serverless Free | $0/month | Shared broker, 100 connections, 10 GB/month, no uptime SLA |
| Valkey | Not provisioned | $0/month | PurpleGlass does not currently consume Valkey at runtime |
| Persistent disks | None | $0/month | Data Protection keys and all service-local files are ephemeral |

The estimated recurring infrastructure cost is **$0/month**, excluding optional provider usage, bandwidth/build overages, and any future upgrades. Render can suspend free services when workspace usage limits are exhausted. Review current pricing before provisioning.

`purpleglass-web` builds the React application into the Web BFF image. Browser API calls, cookies, CSRF, SSE, HTTPS callbacks, and WSS media therefore remain same-origin. `numInstances: 1` is required because realtime voice sessions are process-local.

`purpleglass-integrations-worker` still runs the unchanged .NET worker, transactional outbox publisher, and telephony transport worker. Render does not offer free background-worker instances, so a tiny static health listener makes its container eligible for the free Web Service plan. This listener does not process business work or bypass architectural boundaries.

Render cannot host the private raw-TCP Mosquitto topology for free: private services have no Free plan, and public Web Services expose HTTP/WebSocket traffic rather than a public MQTT TCP listener. The free Blueprint therefore uses an externally created HiveMQ Cloud Serverless cluster over authenticated TLS on port 8883. Local Compose continues using Mosquitto without TLS on port 1883.

## Free-tier behavior

Render free Web Services spin down after 15 minutes without inbound HTTP requests or WebSocket messages and usually take about a minute to start again. A new HTTP request or WebSocket connection wakes the BFF. The worker has no organic inbound application traffic, so request its public `/health/live` URL immediately before a deployment experiment and confirm it remains awake while the experiment runs.

An active Twilio Media Stream sends WebSocket messages and keeps the BFF awake, but a sleeping BFF can cold-start too slowly for an inbound webhook. Before a live call, wake both services and wait for BFF `/health/ready` and worker `/health/live` to return 200. Free infrastructure is not suitable for call-center availability.

On every restart, redeploy, or spin-down, service-local files disappear. PostgreSQL remains authoritative for durable business/outbox data. MQTT messages already published with no retained flag are not a durable business store; the transactional outbox remains the recovery source if publishing fails. HiveMQ Serverless provides no uptime SLA.

The Web BFF has no persistent Data Protection key disk. ASP.NET Data Protection remains enabled with its normal encryption behavior, but keys are ephemeral. Restarting the BFF can invalidate development session and CSRF cookies; users must sign in again. This is acceptable only for synthetic prototype data.

## Setup

1. Create a free HiveMQ Cloud Serverless cluster. Create a least-privilege credential allowed to publish and subscribe to PurpleGlass `pg/...` topics.
2. Push `render.yaml` to the GitHub branch Render tracks and create or update the Render Blueprint.
3. Enter the required `sync: false` values on both services:
   - `Mqtt__Host`: the HiveMQ cluster hostname, without a scheme.
   - `Mqtt__Username`: the HiveMQ access-credential username.
   - `Mqtt__Password`: the HiveMQ access-credential password.
4. Keep the Blueprint defaults that disable real telephony, AI, speech, Open Dental, and sensitive data.
5. Deploy the Blueprint. Automatic deploys wait for GitHub checks.

The BFF reads Render's `PORT` and binds to `0.0.0.0:<PORT>`. The worker image exposes only a static `/health/live` endpoint for Render lifecycle management. Local `Start-PurpleGlass.ps1`, ports 5101/5173, PostgreSQL, Mosquitto, Valkey, frontend, BFF, and worker behavior remain unchanged.

## Migrations and health

Free Web Services do not support Render's paid pre-deploy command. The single BFF container therefore runs the dedicated `PurpleGlass.Migrations` executable before starting the web process. Only the one-instance BFF performs migrations; the worker never migrates. Repeated cold starts safely find the schema current. During a deploy, the old BFF can remain available while the replacement performs migration.

Render uses BFF `/health/live` as its deployment health gate. This endpoint is process/container liveness: it confirms ASP.NET started, the HTTP server is responding, and the process is alive without making temporary external dependency failures restart a functioning service. BFF `/health/ready` remains the independent operational-readiness endpoint; it verifies PostgreSQL connectivity and telephony-provider readiness and should be checked after deployment before an experiment. The worker wrapper's `/health/live` confirms its container is awake; inspect sanitized worker logs to confirm MQTT connectivity and outbox progress.

## Enable Twilio for a cloud-development call

The Blueprint remains safe by default: do not change the disabled provider values in `render.yaml`. Enable Twilio with manual Render environment overrides only for the development experiment. OpenAI is not part of this path.

In the Render Dashboard, set these values on **both** `purpleglass-web` and `purpleglass-integrations-worker`:

- `Providers__EnableRealTelephony=true`
- `Telephony__Provider=Twilio`
- `Telephony__PublicBaseUrl=https://purpleglass-web.onrender.com`
- `Telephony__Twilio__AccountSid=<Twilio Account SID>`
- `Telephony__Twilio__AuthToken=<Twilio Auth Token>`

On **`purpleglass-web` only**, configure the realtime speech path used by the Twilio Media Stream:

- `Providers__EnableRealSpeech=true`
- `SpeechToText__Provider=OpenAI`
- `TextToSpeech__Provider=OpenAI`
- `OpenAI__ApiKey=<OpenAI API key>`

`Providers__EnableRealAI` may remain `false` with `LanguageModel__Provider=Fake` for the bounded development conversation, or it may be enabled with `LanguageModel__Provider=OpenAI` when a real language-model conversation is explicitly required. The fake speech adapter is simulator-only: it emits and accepts synthetic text frames and is not compatible with Twilio's 8 kHz media stream. PurpleGlass now reports that combination as `voice_media_provider_incompatible`, marks readiness degraded, disables the dashboard Call action, and rejects direct outbound requests before creating a durable/billable call.

Store the SID, Auth Token, and OpenAI key only as Render secrets; never place them in the Blueprint, source control, logs, screenshots, or support output. Both services consume the Twilio credentials: the worker submits and updates calls, while the Web BFF verifies signed HTTP and WebSocket callbacks and validates the media Account SID. Only the BFF consumes the OpenAI key for speech recognition and synthesis.

There is deliberately no `Telephony__Twilio__FromNumber` setting. PurpleGlass takes the outbound caller ID from its persisted telephony-number assignment for the administrator's tenant and location. Before calling, use the authenticated `PUT /bff/v1/telephony/numbers` administration endpoint to assign the Twilio number in E.164 format with `provider` set to `Twilio`, the selected `locationId`, `outboundEnabled: true`, and `active: true`. Set `inboundEnabled: true` only if inbound routing will also be tested. The request is CSRF-protected and requires the `ManageLocation` permission; do not create the row manually in PostgreSQL.

Twilio trial calls may target only a recipient verified in the Twilio Console. Enter that verified destination in the PurpleGlass call control in E.164 form, for example `+15551234567`; never hard-code it. The configured source number must be a Twilio number the trial account is allowed to use.

PurpleGlass uses these exact provider routes:

- Outbound answer/TwiML: `POST https://purpleglass-web.onrender.com/telephony/twilio/answer?operationId=<operation-id>` (generated per durable operation; do not configure this manually in Twilio)
- Status callback: `POST https://purpleglass-web.onrender.com/telephony/twilio/status?operationId=<operation-id>` (generated per durable operation)
- Inbound voice webhook: `POST https://purpleglass-web.onrender.com/telephony/twilio/inbound`
- Realtime media: `wss://purpleglass-web.onrender.com/telephony/twilio/media` (returned in TwiML)

Set the Twilio number's incoming Voice webhook to the inbound URL only when inbound calling is required. PurpleGlass reconstructs signature inputs from the canonical public base URL rather than Render's internal HTTP representation. `X-Twilio-Signature` verification is mandatory for all three webhooks and the media WebSocket.

Before the first outbound test, deploy both services after saving their environment changes. Wake `purpleglass-web` and `purpleglass-integrations-worker` by opening their `/health/live` endpoints, wait for `https://purpleglass-web.onrender.com/health/ready` to return 200, and confirm the worker remains awake. Then sign in as the development administrator and call only the verified trial recipient. Do not use an automated test to place the call.

Watch the PurpleGlass call state/dashboard and SSE updates, the Twilio Call log, and sanitized Render logs. Safe failure categories include `telephony_number_unavailable`, `provider_rejected`, `provider_authentication_failed`, `provider_network_error`, `provider_timeout`, `provider_unavailable`, `invalid_provider_signature`, `public_base_url_invalid`, and `provider_disabled`. Twilio's Call log is the appropriate place to distinguish trial-recipient verification errors from other provider rejections. Never copy the Auth Token, authorization headers, signatures, phone numbers, audio, transcripts, or patient information into logs or reports.

To disable Twilio again on both Render services, restore `Providers__EnableRealTelephony=false` and `Telephony__Provider=None`, redeploy, and verify `/health/ready`. Remove the three Twilio-specific values from each service when the experiment is over. Leave the AI and speech toggles false throughout.

Initial provisioning does not request unused Twilio/OpenAI inputs. If OpenAI is enabled in a separate future task, store `OpenAI__ApiKey` only as a Render secret and use the existing provider selectors and safety switches.

`Security__ProductionOrigin` and production OIDC settings are not required while this synthetic cloud environment intentionally runs in `Development`. They become mandatory before moving to `Production`; that upgrade also requires persistent Data Protection keys and the existing server-owned identity mappings.

## Upgrade path

- Upgrade the BFF to a paid Web Service and attach a disk at `/var/data` for stable Data Protection keys; then restore `Security__DataProtectionKeysPath=/var/data/protection`.
- Convert the worker back to a Render background worker on a paid plan and remove its health-listener wrapper from the deployment command.
- Upgrade Postgres before day 30 to retain data, backups, and continuous availability.
- Replace HiveMQ Serverless with a paid managed MQTT plan or restore a paid private Mosquitto service with a persistent disk.
- Add Render Key Value only when PurpleGlass introduces an actual Valkey-backed runtime feature.

## Repeatable cloud smoke test

Run this after each deployment. Use synthetic development data only. The automated phase must not place a phone call.

### Automated/read-only phase

1. Request the public `/` route and confirm HTTP 200 with the PurpleGlass HTML shell.
2. Request `/health/live`; require HTTP 200. A failure means the web process is not serving traffic.
3. Request `/health/ready`; require HTTP 200 before testing business work. If it is degraded, inspect the named `postgres`, `telephony`, and `voiceState` health data and stop.
4. Request the worker's `/health/live` route and confirm HTTP 200, then inspect sanitized worker logs for MQTT subscription and outbox dispatch activity.
5. Sign in with a synthetic development identity, fetch `/bff/v1/session`, and verify the tenant, active location, authorized locations, role, and permissions are present without secrets.
6. Load the dashboard and exercise Dashboard, Calls, Dead letters (when authorized), and Settings navigation. Confirm unsupported appointments remain explicitly labeled as awaiting the Open Dental adapter.
7. In Settings, make a reversible synthetic display-name change through the UI. Confirm the authenticated CSRF-protected request succeeds, the version advances, and the change appears through SSE without a full refresh. Restore the prior synthetic name through the same UI.
8. Verify a read-only user cannot initiate calls, mutate the location, or inspect operations data. Verify an administrator cannot request data for an unauthorized tenant/location.
9. Use provider test doubles or the call simulator to create a non-billable call. Confirm `CallSession`, state changes, conversation/transcript/summary where produced, audit records, and outbox records persist through normal APIs and workers.
10. Confirm duplicate and out-of-order simulated callbacks do not corrupt the final call state, and pending outbox work completes after a worker restart.
11. Keep the dashboard open while the simulated call changes state. Confirm tenant/location-filtered SSE invalidates Redux call data and the visible state changes without a page refresh; then refresh the browser and confirm the persisted state reloads.

Record HTTP statuses, safe error codes, internal call ID, external synthetic provider ID, state transitions, outbox completion, and timestamps. Do not record cookies, headers containing credentials/signatures, phone numbers, patient data, transcripts, or audio.

### One-call provider-live phase

Run only after the automated phase passes and both services are awake. Confirm `/health/ready` is HTTP 200 and its telephony `voiceState` is `ready`. Use only the configured Twilio test number and verified recipient; place no more than one live call.

1. Submit the outbound request through the administrator dashboard and record the internal call ID and start time.
2. Confirm the worker persists the Twilio Call SID, the signed answer callback returns XML containing the canonical `wss://<public-host>/telephony/twilio/media` URL, and the signed status callback advances the existing call.
3. Confirm the media stream remains connected while the conversation is active, audio flows through STT → language model → TTS, and the dashboard updates over SSE.
4. Hang up once through the dashboard or complete the call normally. Confirm the final call state, failure metadata if applicable, conversation summary/outcome, audit record, and outbox completion persist.
5. Correlate PurpleGlass and Twilio timestamps. If provider/account policy ends the call, record the Twilio error code and prove PurpleGlass did not request the termination.

Do not retry a live call to diagnose a failure. Inspect the signed callback responses, Twilio Call log, Render logs, persisted safe state, and worker/outbox state first.

## Troubleshooting

- **Deploy failed:** verify all three MQTT inputs are set and the Render Postgres reference exists. The BFF logs migration failures before web startup.
- **Database unavailable:** confirm the free database has not expired and `ConnectionStrings__Postgres` references its internal URL. Free databases can restart or undergo maintenance without notice.
- **MQTT unavailable:** confirm HiveMQ host, port 8883, TLS, credentials, and topic permissions. Do not disable certificate validation.
- **Worker not processing:** open its `/health/live` URL, wait for cold start, and inspect sanitized logs for MQTT reconnection or database errors.
- **WebSocket cannot connect:** wake the BFF first, require `wss://`, use the exact canonical host, and keep one BFF instance.
- **Twilio 403/signature failure:** make `Telephony__PublicBaseUrl` exactly match the external callback origin and verify the Twilio secret values; never disable validation.
- **Call action disabled with `voice_media_provider_incompatible`:** the BFF is using `Fake` or `Disabled` speech with real Twilio. Configure the BFF's OpenAI speech settings listed above and verify `/health/ready` before trying again. Do not retry a live call while readiness is degraded.
- **OpenAI unavailable:** verify the provider selectors, safety switches, and `OpenAI__ApiKey`. Fake providers require no key.
- **Session disappeared:** a BFF restart replaced its ephemeral Data Protection keys; clear stale cookies and sign in again.
- **Render deployment health failed:** check BFF `/health/live` and sanitized startup logs. After deployment, check `/health/ready`; if it is not healthy, restore the affected operational dependency before an experiment.

OpenTelemetry export remains optional. Never log or commit connection strings, MQTT/Twilio/OpenAI credentials, provider signatures, phone numbers, transcripts, prompts, or audio.
