# ADR 0007: OpenTelemetry observability boundary

## Status

Accepted.

## Decision

PurpleGlass uses BCL `ActivitySource`, `Meter`, and `ILogger` in shared/application code. OpenTelemetry SDK registration belongs only at host composition roots. Stable sources are `PurpleGlass.WebBff`, `PurpleGlass.CallOrchestrator`, `PurpleGlass.Eventing`, `PurpleGlass.Messaging`, `PurpleGlass.Security`, and `PurpleGlass.Integrations`.

Business correlation IDs remain distinct from trace IDs. Durable messages retain correlation/causation/message identifiers plus nullable W3C `traceparent` and `tracestate`. MQTT transports these values as user properties; topics never carry tracing context. Older outbox rows without W3C fields remain valid.

OTLP is optional and never a readiness dependency. Telemetry excludes payloads, transcripts, prompts, credentials, tokens, cookies, and high-cardinality identifiers from metrics.
