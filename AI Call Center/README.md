# PurpleGlass AI Call Center

Multi-tenant AI call-center platform initially designed for dental offices.

## Documentation

- [Application design and pre-coding plan](./APPLICATION_DESIGN.md)
- [Architecture documentation guide](./docs/architecture/README.md)
- [Repository folder catalog](./docs/architecture/REPOSITORY_FOLDER_CATALOG.md)
- [Backend modules and dependencies](./docs/architecture/BACKEND_MODULES_AND_DEPENDENCIES.md)
- [Frontend and BFF relationships](./docs/architecture/FRONTEND_AND_BFF_RELATIONSHIPS.md)
- [Runtime and integration flows](./docs/architecture/RUNTIME_AND_INTEGRATION_FLOWS.md)
- [Prototype 1 delivery guide](./docs/prototype/README.md)

The repository contains the approved architecture documentation and the initial Prototype 1 backend foundation.

## Development prerequisites

- .NET SDK 10.0.302
- Node.js 24 LTS and npm
- Docker Desktop with Docker Compose

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
