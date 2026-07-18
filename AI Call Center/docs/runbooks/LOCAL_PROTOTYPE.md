# Local Prototype Runbook

This starts the synthetic, patient-free React-to-backend prototype. Run commands from `AI Call Center`.

## First-time setup

```bash
docker compose up -d
dotnet tool restore
dotnet restore src/backend/PurpleGlass.sln --locked-mode
dotnet run --project src/backend/Hosts/PurpleGlass.Migrations/PurpleGlass.Migrations.csproj
npm ci --prefix src/frontend
```

The migrations host applies pending EF Core migrations and seeds one deterministic synthetic dental tenant and location.

## Start

Open three terminals.

```bash
# Terminal 1: BFF
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5101 dotnet run --project src/backend/Hosts/PurpleGlass.WebBff/PurpleGlass.WebBff.csproj

# Terminal 2: outbox/MQTT worker
DOTNET_ENVIRONMENT=Development dotnet run --project src/backend/Hosts/PurpleGlass.Integrations.Worker/PurpleGlass.Integrations.Worker.csproj

# Terminal 3: React
npm run dev --prefix src/frontend -- --host 127.0.0.1
```

Open <http://127.0.0.1:5173>. During development, Vite proxies browser `/bff/*` calls to the BFF.

## Verify

```bash
docker compose ps
curl http://127.0.0.1:5101/health/ready
curl http://127.0.0.1:5101/bff/v1/tenant-summary
npm run build --prefix src/frontend
dotnet build src/backend/PurpleGlass.sln --no-restore
dotnet test src/backend/PurpleGlass.sln --no-build --no-restore
```

Changing the office name proves this flow:

```text
React → Redux/RTK Query → BFF → PostgreSQL
                             ├→ audit
                             └→ outbox → worker → MQTT → BFF → SSE → UI refresh
```

## Safety and stop

The synthetic session is development-only and is not authentication. Do not enter real patients, appointments, phone numbers, credentials, or protected health information. Open Dental, telephony, voice, and AI are not connected yet.

Stop application processes with `Ctrl+C`, then run `docker compose down`. Named volumes are retained.
