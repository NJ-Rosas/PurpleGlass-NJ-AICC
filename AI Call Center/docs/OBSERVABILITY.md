# Observability

Each host registers the shared PurpleGlass OpenTelemetry pipeline. Incoming HTTP, HttpClient, runtime, and Npgsql diagnostics are collected alongside custom call, adapter, eventing, MQTT, security, and SSE telemetry. Export is disabled by default and can be enabled with the `Observability__Otlp__*` settings in `.env.example`.

## Following one workflow

Start with the response `X-Correlation-Id`. Search structured logs for `CorrelationId`, then use `TraceId`/`SpanId` to navigate the execution trace. Outbox records retain `CorrelationId`, `CausationId`, `TraceId`, `TraceParent`, and `TraceState`. MQTT user properties continue that trace; SSE intentionally exposes only the sanitized correlation GUID as the event `id`.

Call traces use `call.orchestrate`, `speech.recognize`, `ai.generate`, and `speech.synthesize`. Outbox/MQTT spans use `outbox.*`, `mqtt.publish`, and `mqtt.consume`.

## Data safety

Never add transcripts, speech, prompts, responses, contact details, tokens, cookies, secrets, connection strings, or event payloads to telemetry. Call, conversation, message, user, tenant, and correlation identifiers may be trace/log properties but must not be metric dimensions. Errors use bounded codes, not raw provider or exception messages.

## Local OTLP

Run any OTLP-compatible collector on a private development interface, set `Observability__Otlp__Enabled=true`, and configure its HTTP(S) endpoint. No collector is required for normal development, tests, liveness, or readiness.
