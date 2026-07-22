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

## Simulated AI calls

Task 4 runs the provider-neutral call pipeline locally without telephony, paid AI, or paid speech providers. Start PostgreSQL and apply migrations first:

```bash
docker compose up -d postgres
dotnet run --project src/backend/Hosts/PurpleGlass.Migrations/PurpleGlass.Migrations.csproj
```

Run either synthetic direction:

```bash
dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- inbound
dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- outbound
```

Each command prints the call direction, durable state transitions, caller and assistant turns, escalation status, final summary, IDs, and persisted final states. The sample uses only synthetic phone numbers and creates a unique provider/idempotency key for each run.

The Call Orchestrator depends on `ISpeechRecognizer`, `IAiConversationRuntime`, and `ISpeechSynthesizer`. Task 4 registers deterministic implementations from `Adapters/Speech` and `Adapters/AI`. A real provider replaces those registrations without changing Call Management, Conversation, or orchestration code.

Configuration is under `CallOrchestrator` in the worker settings and may be overridden with standard double-underscore environment variables. Supported prototype settings include language, `calm-a` or `bright-b` voice, greeting, office facts, safety keywords, maximum turns, timeouts, adapter keys, deterministic delays, and failure flags. For example:

```bash
CallOrchestrator__Conversation__VoiceId=bright-b dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- inbound
CallOrchestrator__MockAi__FailGeneration=true dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- inbound
CallOrchestrator__MockSpeech__FailRecognition=true dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- inbound
CallOrchestrator__MockSpeech__FailSynthesis=true dotnet run --project samples/PurpleGlass.CallSimulator/PurpleGlass.CallSimulator.csproj -- outbound
```

Failure simulation should end with both durable aggregates in `Failed`. Human or urgent keywords complete with an escalation outcome. Adapter retries are bounded, and cancellation performs bounded cleanup.

Known limitations: synthesized audio is an opaque in-memory reference, not human-quality audio; there is no telephone number, streaming audio, object storage, MQTT dispatcher change, browser, or dashboard integration in Task 4.

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

The synthetic session is development-only and is not authentication. Do not enter real patients, appointments, phone numbers, credentials, or protected health information. Open Dental and real telephony, speech, and AI providers are not connected.

Stop application processes with `Ctrl+C`, then run `docker compose down`. Named volumes are retained.
