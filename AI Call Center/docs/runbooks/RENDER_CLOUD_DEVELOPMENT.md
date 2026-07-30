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

`purpleglass-integrations-worker` runs the .NET transactional outbox publisher and telephony transport worker as a free Web Service. The same .NET process exposes lightweight liveness and dependency-aware readiness endpoints; there is no separate static health listener.

Render cannot host the private raw-TCP Mosquitto topology for free: private services have no Free plan, and public Web Services expose HTTP/WebSocket traffic rather than a public MQTT TCP listener. The free Blueprint therefore uses an externally created HiveMQ Cloud Serverless cluster over authenticated TLS on port 8883. Local Compose continues using Mosquitto without TLS on port 1883.

## Free-tier behavior

Render free Web Services spin down after 15 minutes without inbound HTTP requests or WebSocket messages and usually take about a minute to start again. A new HTTP request or WebSocket connection wakes the BFF. The worker has no organic inbound application traffic, so request its public `/health/live` URL immediately before a deployment experiment and confirm it remains awake while the experiment runs.

An active Twilio Media Stream sends WebSocket messages and keeps the BFF awake, but a sleeping BFF can cold-start too slowly for an inbound webhook. Before a live call, wake both services and wait for BFF `/health/ready` and worker `/health/live` to return 200. Free infrastructure is not suitable for call-center availability.

On every restart, redeploy, or spin-down, service-local files disappear. PostgreSQL remains authoritative for durable business/outbox data. MQTT messages already published with no retained flag are not a durable business store; the transactional outbox remains the recovery source if publishing fails. HiveMQ Serverless provides no uptime SLA.

The Web BFF has no persistent Data Protection key disk. ASP.NET Data Protection remains enabled with its normal encryption behavior, but keys are ephemeral. Restarting the BFF can invalidate development session and CSRF cookies; users must sign in again. This is acceptable only for synthetic prototype data.

## Setup

1. Create a free HiveMQ Cloud Serverless cluster. Create a least-privilege credential allowed to publish and subscribe to PurpleGlass `pg/...` topics.
2. Push `render.yaml` to the GitHub branch Render tracks and create or update the Render Blueprint. Non-secret cloud-development configuration comes from this file and must not be maintained as competing manual service overrides.
3. Enter the required Render-managed `sync: false` values:
   - `Mqtt__Host`: the HiveMQ cluster hostname, without a scheme.
   - `Mqtt__Username`: the HiveMQ access-credential username.
   - `Mqtt__Password`: the HiveMQ access-credential password.
   - On `purpleglass-web`: `OpenAI__ApiKey`, `Telephony__Twilio__AccountSid`, and `Telephony__Twilio__AuthToken`.
   - On `purpleglass-integrations-worker`: `Telephony__Twilio__AccountSid` and `Telephony__Twilio__AuthToken`.
4. Sync the Blueprint, then deploy both services. Automatic deploys wait for GitHub checks.

For an existing Blueprint, newly declared `sync: false` variables have names in source control but still require their values to be entered in the Render Dashboard. Blueprint synchronization never supplies a secret value.

### Configuration precedence

.NET loads the host's `appsettings.json`, then the environment-specific `appsettings.Development.json` when present, and finally environment variables. Environment variables use double underscores to map nested keys, so `Providers__EnableRealSpeech` overrides `Providers:EnableRealSpeech` from JSON. On Render, Blueprint sync applies the non-secret environment values declared in `render.yaml`; Render-managed `sync: false` values provide only the corresponding secrets/manual external inputs. A later service deploy consumes the environment already attached to that service but does not itself synchronize a changed Blueprint.

The BFF and integrations worker read Render's `PORT` and bind to `0.0.0.0:<PORT>`. Worker `/health/live` proves only that the .NET process and HTTP server respond. Worker `/health/ready` verifies PostgreSQL connectivity, the actual publisher MQTT connection, a successful worker processing cycle, and required telephony-provider configuration. Local `Start-PurpleGlass.ps1`, ports 5101/5173, PostgreSQL, Mosquitto, Valkey, frontend, and BFF behavior remain unchanged.

## Migrations and health

Free Web Services do not support Render's paid pre-deploy command. The single BFF container therefore runs the dedicated `PurpleGlass.Migrations` executable before starting the web process. Only the one-instance BFF performs migrations; the worker never migrates. Repeated cold starts safely find the schema current. During a deploy, the old BFF can remain available while the replacement performs migration.

Render uses each service's `/health/live` as its deployment health gate. Liveness confirms the relevant .NET process and HTTP server respond without making temporary dependency failures restart a functioning service. BFF `/health/ready` verifies PostgreSQL and telephony/realtime-voice readiness. Worker `/health/ready` independently reports `postgres`, `mqtt`, `worker`, and `telephony` components and must be healthy before an experiment.

Expected optimistic-concurrency conflicts during telephony completion are recovered at the individual dispatch boundary. The provider action is never repeated: completion is retried a maximum of three times in fresh scopes that reload current durable state. A result already completed by a concurrent callback converges idempotently, terminal call state is not overwritten, exhausted conflicts are logged with safe identifiers, and the worker continues. Unexpected exceptions are not globally ignored and retain the host's fatal-failure behavior.

Operationally, `live` deployment metadata proves only which image Render selected; `/health/live` proves the current process responds; `/health/ready` proves its required dependencies and processing state are usable. A 503 readiness response with a named component is dependency degradation, not by itself a liveness failure.

## Task 10 Twilio cloud-development configuration

The Blueprint is the source of truth for every non-secret Task 10 setting. It deterministically configures real Twilio transport and real OpenAI speech while retaining the fake language model:

| Capability | Web BFF | Integrations worker |
| --- | --- | --- |
| Telephony | `Providers__EnableRealTelephony=true`, `Telephony__Provider=Twilio` | Same |
| Public callback origin | `Telephony__PublicBaseUrl=https://purpleglass-web.onrender.com` | Same |
| Speech | `Providers__EnableRealSpeech=true`, STT/TTS `OpenAI` | Not consumed or configured |
| Language model | `Providers__EnableRealAI=false`, provider `Fake` | Not consumed or configured |
| Open Dental / sensitive data | Disabled | Not applicable |

Do not manually edit those variables for each deploy. A manual **service deploy** selects a source commit and rebuilds that service, but it does not apply changes from `render.yaml`. **Blueprint synchronization** applies the declared environment configuration and can replace conflicting manual dashboard values. This precedence is why the old Blueprint restored `Telephony__Provider=None` and `Providers__EnableRealSpeech=false`.

Only secret values remain manual. Store the Twilio SID, Twilio Auth Token, OpenAI key, and MQTT credentials only in Render; never place them in the Blueprint, source control, logs, screenshots, or support output. Both services consume the Twilio credentials. Only the BFF consumes the OpenAI key for speech recognition and synthesis.

The fake speech adapter is simulator-only: it emits and accepts synthetic text frames and is not compatible with Twilio's 8 kHz media stream. PurpleGlass reports that combination as `voice_media_provider_incompatible`, marks readiness unavailable, disables the dashboard Call action, and rejects direct outbound requests before creating a durable/billable call.

### Free-tier worker wake and stale-call safety

When the integrations worker runs as a Render Free web service, MQTT and PostgreSQL activity do not wake it. The web BFF therefore probes the worker's configured `/health/ready` URL before it creates an outbound call. That probe wakes a sleeping worker and waits for PostgreSQL, MQTT, processing-loop, and telephony readiness. If readiness does not recover within the bounded window, the BFF returns `telephony_runtime_unavailable` and creates no durable or provider call.

Signed inbound Twilio requests enqueue a background worker wake without delaying the TwiML response. The inbound request itself wakes the free web BFF, so the first webhook can still experience Render cold-start latency. Paid always-on services remain the production recommendation.

The worker rejects an outbound operation older than `Telephony__MaximumQueueAgeSeconds` without contacting the provider. The operation and call fail with `telephony_runtime_unavailable`, preventing a stale queued request from causing a delayed surprise call after the worker returns.

There is deliberately no `Telephony__Twilio__FromNumber` setting. PurpleGlass takes the outbound caller ID from its persisted telephony-number assignment for the administrator's tenant and location. Before calling, use the authenticated `PUT /bff/v1/telephony/numbers` administration endpoint to assign the Twilio number in E.164 format with `provider` set to `Twilio`, the selected `locationId`, `outboundEnabled: true`, and `active: true`. Set `inboundEnabled: true` only if inbound routing will also be tested. The request is CSRF-protected and requires the `ManageLocation` permission; do not create the row manually in PostgreSQL.

Twilio trial calls may target only a recipient verified in the Twilio Console. Enter that verified destination in the PurpleGlass call control in E.164 form, for example `+15551234567`; never hard-code it. The configured source number must be a Twilio number the trial account is allowed to use.

PurpleGlass uses these exact provider routes:

- Outbound answer/TwiML: `POST https://purpleglass-web.onrender.com/telephony/twilio/answer?operationId=<operation-id>` (generated per durable operation; do not configure this manually in Twilio)
- Status callback: `POST https://purpleglass-web.onrender.com/telephony/twilio/status?operationId=<operation-id>` (generated per durable operation)
- Inbound voice webhook: `POST https://purpleglass-web.onrender.com/telephony/twilio/inbound`
- Realtime media: `wss://purpleglass-web.onrender.com/telephony/twilio/media` (returned in TwiML)

Set the Twilio number's incoming Voice webhook to the inbound URL only when inbound calling is required. PurpleGlass reconstructs signature inputs from the canonical public base URL rather than Render's internal HTTP representation. `X-Twilio-Signature` verification is mandatory for all three webhooks and the media WebSocket.

### Task 10 live evidence and remaining validation

A controlled synthetic Task 10 call has proven the complete multi-turn path in the Render environment: outbound submission to Twilio, phone answer, signed answer/status callbacks, bidirectional Media Stream establishment, an audible OpenAI TTS greeting, inbound caller audio, successful OpenAI STT, multiple persisted caller and fake-agent turns, repeated TTS playback, realtime dashboard updates, 11 finalized transcript turns, normal human hangup, and completed durable call and conversation state. The fresh-scope persistence fix remained stable with no recurrence of the earlier EF transaction/cancellation failure. Do not repeat those provider transactions merely to reconfirm an earlier checkpoint.

That call also exposed an intermittent word-tail hiss. Deterministic analysis traced it to unfiltered 24 kHz to 8 kHz decimation: high-frequency energy in some synthesized fricatives folded into the audible telephone band. The corrected path applies one response-contiguous, windowed-sinc low-pass resampling operation, so source chunk boundaries do not reset filter state. It does not trim words, add fades, or append synthetic silence. The realtime implementation continues to use a fresh short database scope for each mutation; it does not retain a `DbContext` or transaction across speech duration, OpenAI calls, WebSocket I/O, or TTS playback.

The first controlled call after that anti-alias change exposed a separate live regression. OpenAI speech requests returned HTTP 200 and assistant turns were durable, but no assistant audio was audible. At the same time, isolated inbound frames were immediately treated as speech, repeatedly canceled the active response and sent Twilio `clear`, then became short STT submissions after trailing silence. OpenAI produced non-empty, sometimes multilingual text from that under-qualified audio; those results became caller turns until the unchanged `maximum_turns` safety guard stopped the loop and the session failed. The provider stream parser was already discarding `outbound` track events, so direct Twilio outbound-track loopback was not the source. Acoustic echo remains possible at the handset and is handled as inbound audio; it must satisfy the same speech qualification window.

The hardened implementation requires 120 ms of contiguous energy-qualified PCM before declaring speech, retaining those candidate frames as the utterance pre-roll. Digital/μ-law silence, low-level noise, and a short transient are discarded before STT. Inbound ownership uses Twilio media `chunk` and `timestamp`; repeated chunks are dropped and backwards timestamps are rejected. A finalized turn ID is submitted at most once. This leaves `maximum_turns` unchanged but ensures unqualified or duplicate internal work does not consume it.

The next live validation confirmed that synthesized voice was audible, the earlier word-tail "fff" artifact was gone, real multi-turn conversation and persistence worked, and normal completion remained healthy. It also showed that fixed energy plus 120 ms alone still allowed occasional non-intentional audio to reach STT, and intentional caller speech often did not stop an already playing assistant response promptly. The retained evidence has no raw audio, so it does not prove whether the non-intentional input was handset noise, room noise, breath, or acoustic echo. The software condition was measurable: any sustained above-threshold waveform could qualify without voice-shape or adaptive-noise checks.

The final Task 10 speech gate keeps the 120 ms confirmation latency but adds an adaptive recent-silence RMS floor, separate speech-start and continuation thresholds, and lightweight crest-factor and zero-crossing checks. A candidate must contain a configured ratio of speech-like frames; a 300 ms above-threshold hiss/noise candidate is rejected without clearing playback. Short speech-like 120 ms fixtures remain accepted. This is deliberately a lightweight telephone VAD, not language filtering or acoustic echo cancellation.

The following live call confirmed clean voice quality, silence without phantom turns, short real speech, and durable multi-turn behavior, but barge-in still failed. Sanitized outbound summaries showed multi-second μ-law responses with only six or seven WebSocket media messages, followed by `MarkSent=true`, `Cleared=false`, and `Canceled=false`. For example, 44,000 μ-law bytes represent 5.5 seconds at 8 kHz but six messages match the legacy `ceil(44000 / 8192)` framing, not the 55 messages required by 100 ms audio packets. Code inspection proved that commit `6c08480` already derived 800-byte payloads, so those live summaries came from a runtime still executing the earlier 8 KiB implementation. A safe startup event now reports the application informational version, 100 ms packet duration, derived 800-byte payload, 8 KiB safety ceiling, and monotonic pacing mode so deployment identity and effective transport configuration can be verified without secrets.

The same inspection found two independent playback-lifecycle defects in `6c08480`: the WebSocket write semaphore remained held during pacing delays, and session response ownership ended immediately after the mark was sent rather than when Twilio acknowledged it. The corrected transport holds the response sequencing gate across one response but acquires the WebSocket write semaphore only for each individual media, mark, or clear write. Media is scheduled against a monotonic response timeline, keeping send-ahead to one 100 ms packet. The session waits on response-specific playback state until the matching mark acknowledgement, `clear`, stream termination, or session cancellation. Confirmed speech therefore finds and cancels playback even after all local media has been sent, while late marks for cleared responses are ignored.

The Task 16 production baseline used full-response buffering in both the OpenAI speech adapter and Twilio resampling path. The retained measurements were 0.60–3.51 seconds for LLM completion (median approximately 1.04 seconds), approximately 0.82–4.79 seconds for TTS completion (median approximately 1.53 seconds), and usually approximately 1.0–2.4 seconds from model completion to first audible media, with one approximately 5.3-second outlier. End-to-end caller pauses could reach roughly 3–6 seconds or longer. Several early persisted assistant messages were also reported as inaudible, but the evidence did not establish whether media startup, cancellation, or buffering caused that symptom.

Task 16.1 changes the local outbound architecture without claiming a measured cloud improvement. The hardened pipeline is:

1. The installed official OpenAI .NET SDK streams requested raw `pcm`: signed 16-bit little-endian, 24 kHz, mono, with no container header.
2. A bounded session channel forwards provider audio deltas without waiting for full speech completion. Split PCM16 samples are carried safely across arbitrary delta boundaries.
3. One response-scoped windowed-sinc converter preserves filter history and lookahead across deltas, low-pass filters and resamples to signed 16-bit PCM at 8 kHz, and performs one exact final flush without capacity padding or a duplicate tail.
4. The standard G.711 μ-law encoder converts every valid 8 kHz sample. As soon as 100 ms/800 bytes are available, PurpleGlass begins the existing monotonic packet schedule; send-ahead remains approximately one packet.
5. A response-specific generation remains active throughout paced sending. Every packet and pacing interval observes cancellation. A confirmed barge-in invalidates that generation, cancels remaining sends, then serializes Twilio `clear`; no later packet or mark from the canceled generation can follow it.
6. A response-specific `mark` follows only a normally completed response. Mark acknowledgements remove pending playback state. `clear` cancels pending marks, so a late acknowledgement cannot restore a canceled response.

The media WebSocket factory authenticates and validates Twilio `connected` and `start`, including the current stream SID, before constructing the voice session. The session also awaits the explicit media-ready contract before sending the first greeting chunk. If greeting PCM arrives first, only the bounded synthesis channel holds it; it is released once when ready and is not regenerated. Disconnect, cancellation, or the existing bounded session/startup timeout tears down the wait and streaming resources. Do not add startup sleeps.

Safe latency diagnostics use monotonic durations for endpoint/STT, persistence, LLM, TTS first audio/completion, first carrier media, final media, mark, acknowledgement, clear, and total turn boundaries. Metrics use bounded provider/result/adapter dimensions only. Never add transcript, prompt, generated text, raw/Base64 audio, phone number, patient data, credentials, or arbitrary provider errors to diagnose latency.

Every completed send now returns and logs structured counts for source PCM bytes/samples, resampled samples, μ-law bytes, media-message count, and mark emission. A synthesis response with no final chunk, zero PCM, zero μ-law bytes, zero media messages, or no mark fails safely instead of appearing successful. Inbound diagnostics record only frame count, duration, qualification/submission booleans, and a language-neutral discard reason. Neither diagnostic contains audio, Base64, transcript text, phone numbers, or credentials.

Transport-quality symptoms include hiss following fricatives, clicks at regular source-chunk intervals, delayed audio from an interrupted response, or a malformed/absent greeting while the call remains connected. Diagnose these with the deterministic audio tests and safe call/state correlation first. Never log audio or Base64 payloads. The implementation has no pooled outbound audio buffers; response, resampled, encoded, and serialized buffers use their written lengths and are cleared after use.

Task 16.1 is local implementation only. Do not deploy it, sync the Blueprint, restart production, mutate Render/Twilio configuration, or place a provider call as part of this task. After release approval, use a separate controlled Task 16.2 validation from the exact Task 16.1 SHA. Wake both free services, require healthy readiness, then make at most one authorized human call and compare endpoint-to-STT, LLM duration, TTS time-to-first-audio, endpoint-to-first-Twilio-media, perceived reply latency, and greeting audibility with the baseline above. Record only safe internal identifiers, bounded stages/results, and durations.

For an existing Blueprint, use this exact workflow:

1. Push the corrected Task 10 branch/commit. Sync the Blueprint only when that commit changes `render.yaml`; an application-only deploy does not require a no-op Blueprint sync.
2. Confirm the Render Blueprint tracks that branch and commit.
3. If `render.yaml` changed, sync the Blueprint configuration.
4. Confirm all required `sync: false` secret values already exist; enter missing values manually without exposing them.
5. Deploy both services from the same intended commit.
6. Wake both free services through their `/health/live` endpoints.
7. Require BFF `/health/live` to return HTTP 200.
8. Require BFF `/health/ready` to return HTTP 200 with healthy PostgreSQL and telephony/voice checks.
9. Sign in again if the BFF restarted and its ephemeral Data Protection keys invalidated the old development session.
10. Make one controlled call to the verified trial recipient, answer, and hear the initial greeting.
11. Remain silent for 10 seconds; verify zero caller turns and zero phantom responses.
12. Say one short valid answer such as "Yes" and hear exactly one assistant response.
13. Remain silent again and verify no extra response appears.
14. Start a turn that produces a longer assistant response.
15. Interrupt after approximately one second of assistant playback.
16. Confirm the old audio stops promptly, the caller utterance is recognized once, and a new response plays normally.
17. Complete two or three additional caller/assistant turns.
18. Listen specifically for word-tail hiss, clicks, or static.
19. Hang up normally, then confirm completed call/conversation state and the durable transcript after refresh.
20. Confirm the session is `Completed`, not `Failed`, and that `maximum_turns` did not occur unless the configured number of legitimate caller utterances was genuinely reached.

Do not place the call when Blueprint sync is pending, a required secret is missing, or readiness is not HTTP 200.

Watch the PurpleGlass call state/dashboard and SSE updates, the Twilio Call log, and sanitized Render logs. Safe failure categories include `telephony_number_unavailable`, `provider_rejected`, `provider_authentication_failed`, `provider_network_error`, `provider_timeout`, `provider_unavailable`, `invalid_provider_signature`, `public_base_url_invalid`, and `provider_disabled`. Twilio's Call log is the appropriate place to distinguish trial-recipient verification errors from other provider rejections. Never copy the Auth Token, authorization headers, signatures, phone numbers, audio, transcripts, or patient information into logs or reports.

Task 10 intentionally uses `RealTelephony=true`, `RealSpeech=true`, and `RealAI=false`. To disable the live path later, change the non-secret Blueprint values together in a reviewed follow-up and sync the Blueprint; do not create long-lived manual overrides that drift from source control.

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
3. Confirm the media stream remains connected while the conversation is active. Verify at least two complete caller → STT → persisted caller turn → fake language-model response → persisted agent turn → TTS cycles, with the dashboard updating over SSE.
4. Hang up once through the dashboard or complete the call normally. Confirm both `CallSession` and `Conversation` reach their normal completed states and that transcript metadata, summary/outcome, audit record, and outbox completion persist after a browser refresh.
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
- **Realtime persistence failure:** correlate the structured `Realtime voice session exception` entry by internal CallId, ConversationId, CorrelationId, stage, safe code, and exception type. `voice_persistence_conflict`, `conversation_state_conflict`, and `voice_persistence_failed` distinguish durable failures without exposing SQL values or transcript text. Do not retry a live call until the failing stage is understood.
- **Session disappeared:** a BFF restart replaced its ephemeral Data Protection keys; clear stale cookies and sign in again.
- **Render deployment health failed:** check BFF `/health/live` and sanitized startup logs. After deployment, check `/health/ready`; if it is not healthy, restore the affected operational dependency before an experiment.

OpenTelemetry export remains optional. Never log or commit connection strings, MQTT/Twilio/OpenAI credentials, provider signatures, phone numbers, transcripts, prompts, or audio.
