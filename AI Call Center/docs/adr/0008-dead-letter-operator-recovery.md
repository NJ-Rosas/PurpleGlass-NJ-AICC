# ADR 0008: Dead-letter operator recovery

## Status

Accepted.

## Decision

Terminal delivery failures remain the existing outbox row; PurpleGlass does not create a second queue. Recovery is one atomic tenant/location-scoped conditional update from `DeadLetter` to `Pending`. It records operator, time, recovery correlation, and count while preserving attempts, sanitized failure code, last attempt, and original dead-letter time. The normal dispatcher performs all later delivery.

Viewing and recovery use separate Identity permissions. Tenant administrators receive both through the existing role mapping; read-only users receive neither. BFF authentication, authorization, CSRF, rate limiting, audit, and tenant/location scoping apply. Responses and realtime recovery notifications contain safe metadata only and never the serialized message payload.

Recovery creates a new execution trace through the normal dispatcher while preserving the business correlation ID and durable prior trace metadata. The recovery request has its own correlation ID stored as `LastRecoveryCorrelationId`.
