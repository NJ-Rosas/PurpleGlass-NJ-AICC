# ADR 0002: Use Server-Sent Events for Prototype Realtime Updates

- Status: Accepted
- Date: 2026-07-18

## Context

Prototype 1 needs one-way, authorized notifications from the Web BFF to the React dashboard after an internal MQTT event is processed. The browser must not connect to MQTT.

## Decision

Use Server-Sent Events (SSE) for Prototype 1. The BFF owns the SSE endpoint, authenticates the browser session, filters every event by tenant/location/role, and emits a small versioned browser contract. React uses one reconnecting realtime client and re-queries authoritative RTK Query state after reconnection.

## Consequences

- The implementation is simpler than a bidirectional WebSocket protocol for one-way updates.
- Browser commands continue to use normal BFF HTTP endpoints.
- Scale-out will require a shared BFF projection/backplane strategy before multiple BFF instances are used.
- WebSocket remains an option if future call-control collaboration needs bidirectional low-latency messaging.

