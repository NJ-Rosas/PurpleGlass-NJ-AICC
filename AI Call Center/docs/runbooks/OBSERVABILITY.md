# Observability runbook

## Trace a request or call

1. Capture the `X-Correlation-Id` response header or the simulator correlation ID.
2. Filter logs by `CorrelationId`; note the enriched `TraceId` and `SpanId`.
3. Follow the trace through HTTP/Npgsql, `call.orchestrate`, outbox, `mqtt.publish`, `mqtt.consume`, and `sse.deliver` spans.
4. Use message IDs to inspect durable delivery state without viewing payloads.

## Outbox incidents

Check publish-failure, retry, dead-letter, leased, recovered-lease, age, and duration instruments. Logs provide message ID and attempts. Stored `LastError` is a bounded safe error code. Do not copy payloads into tickets.

## Missing telemetry

Confirm `Observability:Enabled`, endpoint URI, collector reachability, source/meter filters, and service name. Collector failure must not mark application readiness unhealthy. Disable OTLP to isolate exporter issues; application behavior must remain unchanged.
