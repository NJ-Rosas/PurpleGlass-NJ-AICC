# ADR 0005: Eventing-Owned Outbox/Inbox Infrastructure

- Status: Accepted
- Date: 2026-07-18

## Context

Committed module changes must eventually reach MQTT without publishing before database commit. Consumers must tolerate QoS 1 duplicate delivery.

## Decision

The Eventing building block owns the shared `eventing.outbox_messages` and `eventing.inbox_messages` persistence model and processing mechanics. An Application transaction writes its module state, audit intent where required, and outbox record through a shared transaction boundary in the same PostgreSQL transaction.

The outbox worker claims pending rows with bounded batches/leases, publishes versioned envelopes to MQTT with QoS 1, and records success or retry state. Each logical consumer uses an inbox key composed of consumer name and message ID before producing side effects.

Business event definitions remain owned by the publishing module's Contracts project. Eventing knows envelopes and delivery state, not business meaning.

## Consequences

- Modules share reliable delivery infrastructure without sharing domain behavior.
- Publisher and consumers must be idempotent and observable.
- Broker outage increases outbox age but does not roll back committed business state.
- Retention, poison-message handling, and concurrency require explicit configuration and tests.

