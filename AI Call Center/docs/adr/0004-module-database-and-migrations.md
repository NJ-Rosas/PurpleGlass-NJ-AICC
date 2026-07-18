# ADR 0004: Shared PostgreSQL Instance with Module-Owned Schemas

- Status: Accepted
- Date: 2026-07-18

## Context

Prototype 1 uses a modular monolith but needs enforceable data ownership and a path to future service extraction without operating several databases locally.

## Decision

Use one PostgreSQL database instance and database for the prototype, with module-owned schemas. Tenancy owns schema `tenancy`, Audit owns `audit`, and Eventing owns `eventing`. Each module owns its Entity Framework Core model and migrations; no module maps or writes another module's tables.

`PurpleGlass.Migrations` is the only host that applies migrations. Runtime API/BFF/worker startup validates connectivity but never automatically applies migrations.

## Consequences

- Local operations remain simple while ownership is explicit.
- Cross-module database joins and foreign keys are prohibited unless a later ADR proves the need.
- Cross-module coordination uses application contracts or events.
- Migration ordering is controlled by the Migrations host and tested from an empty database.

