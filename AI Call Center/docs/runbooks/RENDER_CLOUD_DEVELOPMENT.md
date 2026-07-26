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

## Provider enablement later

Initial provisioning does not request unused Twilio/OpenAI inputs. When explicitly enabling providers, add the existing configuration names in the Render Dashboard—never in Git:

- `Telephony__PublicBaseUrl`
- `Telephony__Twilio__AccountSid`
- `Telephony__Twilio__AuthToken`
- `OpenAI__ApiKey`
- provider selectors and `Providers__EnableRealTelephony`, `Providers__EnableRealAI`, and `Providers__EnableRealSpeech`

Set `Telephony__PublicBaseUrl` to the exact public HTTPS BFF origin. PurpleGlass reconstructs Twilio signature URLs from this canonical configuration rather than arbitrary Host/forwarded headers and derives `wss://<host>/telephony/twilio/media` from it. Configure the Twilio voice webhook as `POST https://<host>/telephony/twilio/inbound`. Signature verification remains mandatory.

`Security__ProductionOrigin` and production OIDC settings are not required while this synthetic cloud environment intentionally runs in `Development`. They become mandatory before moving to `Production`; that upgrade also requires persistent Data Protection keys and the existing server-owned identity mappings.

## Upgrade path

- Upgrade the BFF to a paid Web Service and attach a disk at `/var/data` for stable Data Protection keys; then restore `Security__DataProtectionKeysPath=/var/data/protection`.
- Convert the worker back to a Render background worker on a paid plan and remove its health-listener wrapper from the deployment command.
- Upgrade Postgres before day 30 to retain data, backups, and continuous availability.
- Replace HiveMQ Serverless with a paid managed MQTT plan or restore a paid private Mosquitto service with a persistent disk.
- Add Render Key Value only when PurpleGlass introduces an actual Valkey-backed runtime feature.

## Troubleshooting

- **Deploy failed:** verify all three MQTT inputs are set and the Render Postgres reference exists. The BFF logs migration failures before web startup.
- **Database unavailable:** confirm the free database has not expired and `ConnectionStrings__Postgres` references its internal URL. Free databases can restart or undergo maintenance without notice.
- **MQTT unavailable:** confirm HiveMQ host, port 8883, TLS, credentials, and topic permissions. Do not disable certificate validation.
- **Worker not processing:** open its `/health/live` URL, wait for cold start, and inspect sanitized logs for MQTT reconnection or database errors.
- **WebSocket cannot connect:** wake the BFF first, require `wss://`, use the exact canonical host, and keep one BFF instance.
- **Twilio 403/signature failure:** make `Telephony__PublicBaseUrl` exactly match the external callback origin and verify the Twilio secret values; never disable validation.
- **OpenAI unavailable:** verify the provider selectors, safety switches, and `OpenAI__ApiKey`. Fake providers require no key.
- **Session disappeared:** a BFF restart replaced its ephemeral Data Protection keys; clear stale cookies and sign in again.
- **Render deployment health failed:** check BFF `/health/live` and sanitized startup logs. After deployment, check `/health/ready`; if it is not healthy, restore the affected operational dependency before an experiment.

OpenTelemetry export remains optional. Never log or commit connection strings, MQTT/Twilio/OpenAI credentials, provider signatures, phone numbers, transcripts, prompts, or audio.
