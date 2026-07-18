# ADR 0001: Development Platform Baseline

- Status: Accepted
- Date: 2026-07-18

## Context

The repository needs a reproducible C#/.NET and React/TypeScript toolchain plus local database, MQTT, and cache services before project scaffolding begins. The selected versions must support Intel macOS development and avoid unpinned global application packages.

## Decision

Use the following development baseline:

| Dependency | Selected version | Installation/use |
|---|---:|---|
| .NET SDK | 10.0.302 LTS | Official Microsoft macOS x64 package; pin with `global.json` when the solution is created |
| C# | 14 | Supplied by the selected .NET SDK; project language settings remain centrally controlled |
| Node.js | 24.18.0 LTS | Official Node.js macOS x64 package; declare supported engines when the frontend manifest is created |
| npm | 11.16.0 | Supplied with Node.js 24.18.0; commit a lockfile after frontend scaffolding |
| Docker Desktop | 29.6.1 | Local container engine |
| Docker Compose | 5.3.0 | Local dependency orchestration |
| PostgreSQL | 18.4 | `postgres:18`, currently resolved to `sha256:32ca0af8e77bfb8c6610c488e4691f83f972a3e9e64d3b02facf3ab111ad5500` |
| Eclipse Mosquitto | 2.1.2 | `eclipse-mosquitto:2.1.2-alpine`, currently resolved to `sha256:6f8d8a947c506f8a2290ec65cd4bd2bc7cb4d43fb5f6271f861cb013e2ef9797` |
| Valkey | 8.1.8 | `valkey/valkey:8.1.8-alpine`, currently resolved to `sha256:94365b275456ae14621001c03556c732b1d93a0cdeacc317d1bdd52eba680885` |

PostgreSQL is the authoritative relational store. Mosquitto is the initial local MQTT broker. Valkey is the Redis-protocol-compatible cache implementation behind an application abstraction.

Application NuGet and npm dependencies will be declared only in project manifests and restored through the .NET and npm package managers. They will not be installed globally. Exact dependency versions will be centrally managed through `Directory.Packages.props` and `package-lock.json`.

Object storage, telemetry backend, identity provider, telephony, speech, AI, messaging, and Open Dental client dependencies remain undecided. They will receive separate ADRs after capability, license, compliance, and operational review. The archived MinIO container is not adopted implicitly as the object-storage decision.

## Consequences

- Developers must install the selected .NET and Node.js SDKs before scaffolding or building.
- CI must use the same pinned major/minor toolchain and locked package graphs.
- Compose definitions should use explicit image versions and may pin production-like environments to digests.
- PostgreSQL 18 volume configuration must follow the version-specific data-directory behavior of its official container image.
- Cache usage must go through a PurpleGlass application port so the Valkey implementation remains replaceable.
- Upgrades require validation and an ADR update rather than silently changing floating tags.

## Verification

- `dotnet --info` reports the pinned SDK after installation.
- `node --version` and `npm --version` report the selected LTS toolchain after installation.
- `docker image inspect` reports the documented service digests.
- Future CI checks validate `global.json`, package lockfiles, and Compose image pins.

