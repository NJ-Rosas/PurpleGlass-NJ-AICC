# Prototype 1: Foundation Checklist

This checklist controls the setup work that must be complete before the first business slice expands.

## A. Toolchain

- [x] `global.json` pins .NET SDK 10.0.302.
- [x] Node engine policy requires Node.js 24 LTS.
- [x] npm version and lockfile policy are documented.
- [x] Docker Desktop and Compose versions meet ADR 0001.
- [x] Tool-version verification is available as a documented command/script.
- [ ] No optional .NET workload is installed without a demonstrated requirement.

## B. Repository configuration

- [x] `.editorconfig` exists and applies consistently.
- [x] `.gitignore` excludes IDE, build, package, environment, coverage, secret, and operating-system artifacts.
- [x] Line endings and UTF-8 policy are defined.
- [x] Root README contains exact setup/build/test/start commands.
- [ ] Dependency and license update process is documented.
- [ ] Secret scanning and dependency scanning are planned for CI.

## C. C# build configuration

- [x] Nullable reference types are enabled.
- [x] Implicit usings policy is consistent.
- [x] Warnings policy is centralized.
- [x] Analyzer configuration and package versions are centralized.
- [x] Deterministic/CI build settings are enabled.
- [x] NuGet package versions live in `Directory.Packages.props`.
- [x] Package lock/restore policy is chosen and documented.
- [x] Project names, namespaces, assembly names, and root namespaces follow one convention.

## D. Backend project graph

- [x] Solution contains only projects required for Prototype 1.
- [x] Tenancy has Domain, Application, Infrastructure, Contracts, and Presentation projects.
- [x] Hosts are executable composition roots.
- [x] Shared libraries have a narrow documented purpose.
- [x] Production projects do not reference `PurpleGlass.Testing`.
- [x] Domain projects do not reference ASP.NET, EF Core, MQTT, or provider SDKs.
- [x] Architecture tests validate the intended references.

## E. Frontend foundation

- [ ] React and TypeScript are configured with strict type checking.
- [ ] Redux Toolkit store and typed hooks exist.
- [ ] RTK Query base API points only to the BFF.
- [ ] Router and accessible app shell exist.
- [ ] Formatting, linting, unit-test, and build commands exist.
- [ ] Environment variables exposed to the browser are explicitly allow-listed.
- [ ] No sensitive values or access tokens are persisted in browser storage.
- [ ] Frontend dependency versions are locked.

## F. Docker Compose

- [ ] PostgreSQL uses the approved version and correct PostgreSQL 18 data-volume layout.
- [ ] Mosquitto uses the approved version and checked-in development config.
- [ ] Valkey uses the approved version.
- [ ] Each service has a health check.
- [ ] Services use a project-scoped network and named volumes.
- [ ] Ports are bound only as needed for local development.
- [ ] Credentials are synthetic, loaded from ignored environment configuration, and never production-compatible.
- [ ] Compose validates with `docker compose config`.
- [ ] Startup and shutdown do not remove data unless a clearly named reset command is used.

## G. Configuration and secrets

- [ ] Backend configuration is strongly typed and validated on startup.
- [ ] Environment-specific files contain no committed secrets.
- [ ] `.env.example` uses unmistakably synthetic values.
- [ ] Logging never emits complete connection strings or credentials.
- [ ] Development identity and tenant context are visibly marked as synthetic-only.
- [ ] Production startup cannot accidentally accept the development identity mechanism.

## H. Persistence

- [ ] Tenancy owns its schema/tables.
- [ ] Migrations run through the Migrations host or an explicit developer command.
- [ ] Application startup does not silently apply production migrations.
- [ ] UTC and office time-zone handling conventions are implemented.
- [ ] Optimistic concurrency is available for mutable configuration.
- [ ] Outbox and inbox tables are created with indexes and retention considerations.
- [ ] Database integration tests use disposable isolated data.

## I. Eventing

- [ ] MQTT topics contain opaque IDs, not names, emails, or phone numbers.
- [ ] Standard envelope includes message, type, schema, time, tenant, correlation, causation, trace, producer, and payload fields.
- [ ] QoS, retain flag, expiry, and payload limits are explicit.
- [ ] Publisher uses the transactional outbox.
- [ ] Consumers are idempotent through the inbox.
- [ ] Retry/backoff and poison-message behavior are bounded.
- [ ] Schemas and examples contain synthetic data.

## J. BFF and browser security

- [ ] React has no direct internal API/provider/MQTT access.
- [ ] BFF resolves tenant/location from the server-side session context.
- [ ] Client-supplied tenant IDs are verified against session authorization.
- [ ] CSRF strategy is implemented for cookie-authenticated mutations.
- [ ] Security headers and allowed-origin behavior are tested.
- [ ] BFF models expose only fields required by the screen.
- [ ] Realtime connections and every emitted event are authorized.

## K. Observability

- [ ] Correlation ID enters at BFF/API boundaries and propagates internally.
- [ ] OpenTelemetry traces cover HTTP, database, outbox, MQTT, and consumer work.
- [ ] Structured logs include opaque tenant/correlation context without sensitive payloads.
- [ ] Health and readiness have different semantics.
- [ ] Metrics exist for outbox age, publish failures, consumer failures, and realtime connections.
- [ ] Local troubleshooting explains where to inspect logs and service health.

## L. Tests and CI

- [ ] Unit tests cover domain invariants and application behavior.
- [ ] Integration tests cover PostgreSQL and MQTT behavior.
- [ ] Architecture tests cover project boundaries.
- [ ] Contract tests validate event schema and BFF/OpenAPI shapes.
- [ ] Tenant-isolation negative tests exist.
- [ ] Duplicate/out-of-order or stale-version tests exist where applicable.
- [ ] CI runs restore, format check, build, test, and contract validation.
- [ ] Test fixtures contain synthetic data only.

## Foundation exit decision

- [ ] Every mandatory item above is complete or has an explicitly accepted ADR exception.
- [ ] The current implementation matches the architecture documentation.
- [ ] A new developer can reproduce the environment from the README.
