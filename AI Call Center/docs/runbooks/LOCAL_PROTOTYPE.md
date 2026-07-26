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

## Event delivery reliability

The integrations worker claims bounded outbox batches with PostgreSQL row locks and expiring leases. Multiple worker instances skip rows already claimed by another instance. An expired lease is recoverable after a worker crash.

Failed publishes return to `Pending` with exponential backoff. After the configured maximum attempts, the message moves to `DeadLetter` and is no longer selected automatically. Successful delivery clears lease and error state and records `PublishedAtUtc`.

The `OutboxPublisher` configuration section controls batch size, maximum attempts, lease duration, initial and maximum message retry delays, polling interval, and the delay after infrastructure failures. Environment-variable overrides use the normal double-underscore convention, such as:

```bash
OutboxPublisher__MaximumAttempts=5 dotnet run --project src/backend/Hosts/PurpleGlass.Integrations.Worker/PurpleGlass.Integrations.Worker.csproj
```

Inspect synthetic local delivery state:

```bash
docker exec purpleglass-prototype-postgres-1 \
  psql -U purpleglass -d purpleglass \
  -c 'SELECT "Status", count(*) FROM eventing.outbox_messages GROUP BY "Status" ORDER BY "Status";'
```

The Eventing infrastructure also owns `eventing.inbox_messages`. Consumers use `InboxDeduplicationStore.ExecuteOnceAsync` with a stable consumer name and message ID. The handler runs before the inbox transaction commits: duplicates skip the handler, and a failed handler rolls back the receipt so delivery can be retried. Database projection changes should enlist in the same transaction; external side effects must still be independently idempotent.

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

The browser begins unauthenticated. Choose the predefined synthetic administrator or read-only identity. The BFF first issues an HttpOnly antiforgery cookie and returns a request token; React sends that token in `X-CSRF-TOKEN` for login, logout, and mutations. Logout is the avatar button in the left rail. No access token, refresh token, session cookie, tenant selector, or MQTT credential is exposed to JavaScript.

Development authentication requires both the Development environment and `Security:AllowDevelopmentAuthentication=true`. Production requires an HTTPS origin, restricted `AllowedHosts`, persistent data-protection keys, complete OIDC authorization-code configuration, and server-owned identity/membership mappings. It rejects development authentication, real provider flags, Open Dental, sensitive-data mode, and disabling synthetic-only mode.

Role permissions in this slice:

| Capability | Tenant administrator | Office manager | Staff | Read-only |
|---|---:|---:|---:|---:|
| View calls/transcripts/summaries | Yes | Yes | Yes | Yes |
| Initiate outbound calls | Yes | Yes | Yes | No |
| Manage location settings | Yes | Yes | No | No |
| Manage tenant settings | Yes | No | No | No |
| View audit records | Yes | Yes | No | No |

Run security validation with:

```bash
dotnet test tests/unit/PurpleGlass.UnitTests/PurpleGlass.UnitTests.csproj
dotnet test tests/integration/PurpleGlass.IntegrationTests/PurpleGlass.IntegrationTests.csproj
dotnet test tests/architecture/PurpleGlass.ArchitectureTests/PurpleGlass.ArchitectureTests.csproj
npm test --prefix src/frontend -- --run
```

## Verify

```bash
docker compose ps
curl http://127.0.0.1:5101/health/ready
curl http://127.0.0.1:5101/bff/v1/tenant-summary
curl http://127.0.0.1:5101/bff/v1/calls
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

After running either call simulator, the dashboard lists the durable call. Selecting it loads the tenant- and location-scoped transcript and generated summary from `/bff/v1/calls/{callId}`. Call and conversation MQTT events invalidate the dashboard query through the existing SSE connection.

## Safety and stop

The synthetic session is development-only and is not authentication. Do not enter real patients, appointments, phone numbers, credentials, or protected health information. Open Dental and real telephony, speech, and AI providers are not connected.

Stop application processes with `Ctrl+C`, then run `docker compose down`. Named volumes are retained.
