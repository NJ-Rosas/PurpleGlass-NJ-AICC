# ADR 0003: Use a Guarded Synthetic Session for Prototype 1

- Status: Accepted
- Date: 2026-07-18

## Context

Prototype 1 must prove server-resolved tenant authorization before a production identity provider is selected. Hard-coded tenant trust in browser requests would invalidate that proof.

## Decision

Implement a synthetic development session at the BFF boundary. It supplies a fixed synthetic actor, tenant, location, and role from validated server configuration. It is enabled only when the host environment is `Development` and an explicit `DevelopmentSession:Enabled` setting is true. Startup fails if it is enabled in any other environment.

The browser receives a secure-session representation appropriate to local development and cannot choose arbitrary tenant authorization. Application handlers still validate resource access using the server-provided current-context abstraction.

## Consequences

- The prototype can exercise tenant isolation without prematurely choosing an identity vendor.
- This mechanism is not production authentication and must be visibly labeled in UI and documentation.
- Tests must prove non-Development startup rejects the feature.
- A future identity ADR replaces the session resolver without changing domain ownership.

