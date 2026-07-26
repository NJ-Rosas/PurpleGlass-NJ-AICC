# PurpleGlass AI Call Center

Multi-tenant AI call-center platform initially designed for dental offices.

## Documentation

- [Application design and pre-coding plan](./APPLICATION_DESIGN.md)
- [Current project state and next steps](./docs/PROJECT_STATE.md)
- [Architecture documentation guide](./docs/architecture/README.md)
- [Repository folder catalog](./docs/architecture/REPOSITORY_FOLDER_CATALOG.md)
- [Backend modules and dependencies](./docs/architecture/BACKEND_MODULES_AND_DEPENDENCIES.md)
- [Frontend and BFF relationships](./docs/architecture/FRONTEND_AND_BFF_RELATIONSHIPS.md)
- [Runtime and integration flows](./docs/architecture/RUNTIME_AND_INTEGRATION_FLOWS.md)
- [Realtime voice conversation pipeline](./docs/architecture/REALTIME_VOICE_PIPELINE.md)
- [Telephony transport](./docs/TELEPHONY.md)
- [Observability](./docs/OBSERVABILITY.md)
- [Prototype 1 delivery guide](./docs/prototype/README.md)

The repository contains the approved architecture documentation and the initial Prototype 1 backend foundation.

## Development prerequisites

- .NET SDK 10.0.302
- Node.js 24 LTS and npm
- Docker Desktop with Docker Compose

## One-click local startup

The preferred Windows development flow is:

1. Open the repository in VS Code.
2. Open [`Start-PurpleGlass.ps1`](../Start-PurpleGlass.ps1).
3. Press the PowerShell **Run / Play ▶** button.

The launcher checks the toolchain, starts Docker Desktop when necessary, starts
and health-checks PostgreSQL, MQTT, and Valkey, restores missing dependencies,
applies existing migrations, and opens visible log windows for the Web BFF,
integrations worker, and React frontend. It is safe to run repeatedly.

See the [local prototype runbook](./docs/runbooks/LOCAL_PROTOTYPE.md) for URLs,
manual fallback commands, shutdown behavior, and troubleshooting.

Verify the installed tools from the project root:

```bash
./scripts/verify-toolchain.sh
```

## Backend commands

Run these commands from `src/backend`:

```bash
dotnet restore PurpleGlass.sln --locked-mode
dotnet build PurpleGlass.sln --no-restore
dotnet test PurpleGlass.sln --no-build --no-restore
```

Start the current health-only hosts during development:

```bash
dotnet run --project Hosts/PurpleGlass.WebBff
dotnet run --project Hosts/PurpleGlass.Api
```

The current health routes are `/health/live` and `/health/ready`.

## Configuration conventions

- Committed configuration contains safe defaults only.
- Local secrets and overrides use ignored environment files or .NET user secrets.
- Environment variables use double underscores for nested .NET configuration keys, for example `DevelopmentSession__Enabled`.
- Browser-exposed frontend variables will use the explicit Vite prefix selected during frontend scaffolding.
- Production startup must reject the synthetic development-session mechanism.
