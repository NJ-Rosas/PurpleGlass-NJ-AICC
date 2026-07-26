# Render cloud development

This runbook prepares, but does not itself provision, a persistent PurpleGlass development environment on Render. It is not a production or HIPAA deployment.

## Architecture

`purpleglass-web` is a single Render Web Service. Its multi-stage image builds the React application and serves it from the Web BFF, so browser API calls, cookies, CSRF tokens, SSE, Twilio HTTPS callbacks, and Twilio WSS media all use one origin. The service must remain at one instance because active realtime voice sessions are process-local.

`purpleglass-integrations-worker` is a background worker. It owns outbox publishing and telephony transport polling and has no inbound port.

Render Postgres stores durable business and eventing data. `purpleglass-mqtt` is a private Mosquitto service used by the worker publisher and BFF subscriber; its private-network-only anonymous listener is intentionally limited to this isolated development topology. Render Key Value supplies a private Valkey-compatible resource. PurpleGlass does not currently read Valkey, so it is reserved rather than falsely wired into application state.

The web and MQTT services have persistent disks for data-protection keys and broker persistence respectively. Application business data never uses service-local disk.

## Render setup and deployment

1. Push `render.yaml` to the GitHub branch that Render should track.
2. In Render, create a Blueprint from the repository and review all paid `starter`/database resources before applying it.
3. Supply every environment variable marked `sync: false`. Do not place values in Git.
4. Set `Telephony__PublicBaseUrl` and `Security__ProductionOrigin` to the exact external HTTPS origin, with no path or trailing untrusted host substitution (for example, `https://purpleglass-web.onrender.com`).
5. Deploy. Automatic deploys are configured for passing GitHub checks. The pre-deploy command runs the dedicated migration executable once before the web release starts.

The Docker web process reads Render's `PORT` and binds Kestrel to `0.0.0.0:<PORT>`. Local launch settings and `Start-PurpleGlass.ps1` remain unchanged at ports 5101 and 5173.

## Required secret and configuration names

Render supplies `ConnectionStrings__Postgres` from the managed database. The repository accepts either an Npgsql connection string or Render's `postgresql://` URL.

Configure these names in Render when their provider is enabled:

- `Telephony__PublicBaseUrl`
- `Telephony__Twilio__AccountSid`
- `Telephony__Twilio__AuthToken`
- `OpenAI__ApiKey`
- `Security__ProductionOrigin`

Provider controls retain their existing names: `Providers__EnableRealTelephony`, `Providers__EnableRealAI`, `Providers__EnableRealSpeech`, `Telephony__Provider`, `SpeechToText__Provider`, `LanguageModel__Provider`, and `TextToSpeech__Provider`. The safe Blueprint defaults are disabled/`None`/fake. Twilio also needs the existing number configuration used by the deployment operator; PurpleGlass does not currently define a separate Twilio-number application setting.

OIDC production settings (`Security__Oidc__Authority`, `Security__Oidc__ClientId`, `Security__Oidc__ClientSecret`, and identity mappings) are required before changing the environment to `Production`. The first cloud-development environment deliberately uses synthetic Development authentication and must not hold real patient data.

## Twilio and public routing

Configure the Twilio voice webhook as `POST https://<public-origin>/telephony/twilio/inbound`. PurpleGlass generates status/answer callbacks from the configured canonical origin and converts that origin to `wss://<public-host>/telephony/twilio/media` for bidirectional media.

Signature verification remains enabled. It reconstructs callback URLs from `Telephony__PublicBaseUrl`, not from arbitrary `Host` or forwarded headers. Render terminates TLS, but the application therefore does not globally trust `X-Forwarded-*` input. Cloud development forces Secure cookies explicitly; Render redirects public HTTP to HTTPS at its edge. If a future feature requires client IP or request-scheme reconstruction, add only documented Render proxy ranges rather than clearing ASP.NET's trusted-proxy lists.

## Migrations and health

`dotnet /app/migrations/PurpleGlass.Migrations.dll` is the web service pre-deploy command. Do not add startup migration calls to the web or worker: pre-deploy serialization avoids horizontally scaled migration races.

Render checks `/health/ready`, which includes PostgreSQL and telephony configuration checks. `/health/live` remains a process liveness endpoint. MQTT reconnects asynchronously and is visible in safe logs/telemetry; it is not currently a readiness check.

OpenTelemetry export is optional. Leave `Observability__Otlp__Enabled=false` unless an OTLP endpoint is deliberately configured. Never log or commit connection strings, credentials, signatures, phone numbers, transcripts, prompts, or audio.

## Local versus cloud

Local development continues to use Compose PostgreSQL, Valkey, and Mosquitto, separate Vite and BFF processes, the one-click PowerShell launcher, and Fake/Disabled providers. Render embeds the frontend, uses managed Postgres/Key Value, a private broker, dynamic port binding, persisted data-protection keys, and same-origin HTTPS.

## Troubleshooting

- **Deploy failed:** inspect the failing Docker build stage and confirm the Blueprint root is the repository root. A pre-deploy failure usually indicates missing/invalid database configuration.
- **Database unavailable:** verify the database and services share the region/environment and that `ConnectionStrings__Postgres` references the internal connection string. Run no ad-hoc concurrent migrations.
- **MQTT unavailable:** verify the private service is running on 1883 and both consumers use its Blueprint-provided host. Broker storage is `/mosquitto/data`.
- **WebSocket cannot connect:** confirm Twilio uses `wss://`, the exact canonical public host, and `/telephony/twilio/media`; keep the BFF at one instance.
- **Twilio 403/signature failure:** ensure `Telephony__PublicBaseUrl` exactly matches the externally configured callback origin and that the correct auth token/account SID are present. Do not disable validation.
- **OpenAI unavailable:** confirm all three safety switches/selectors needed by the chosen STT/LLM/TTS path and `OpenAI__ApiKey`; fake providers require no key.
- **Health check failed:** request `/health/ready`, then inspect sanitized logs for the named dependency. `/health/live` succeeding alone is insufficient readiness.

## Known limitations

This repository change does not create or deploy Render resources. Mosquitto uses private-network isolation rather than credentials for this development foundation. Valkey is provisioned but unused. Realtime voice sessions are in BFF memory, so multiple instances, autoscaling, and restart-transparent active calls are unsupported.
