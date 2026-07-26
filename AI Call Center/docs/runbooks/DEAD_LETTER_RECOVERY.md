# Dead-letter recovery runbook

## Lifecycle

```text
Pending → Processing → Failure → Pending retry
                         ↓ attempts exhausted
                     DeadLetter
                         ↓ authorized operator retry
                      Pending → normal dispatcher
```

## Operator workflow

Open **Dead Letters**, filter by safe type/category, select one record, and correlate its correlation/trace IDs with logs and traces. Details intentionally omit payloads, transcripts, prompts, credentials, tokens, cookies, and exception dumps. Choose **Retry**, read the confirmation, and confirm once. Recovery is never direct execution or bulk replay.

Only tenant administrators with `operations.deadletters.view` can inspect records and those with `operations.deadletters.recover` can retry. Tenant and authorized-location filters apply even to message-ID lookup, so cross-tenant records appear not found. Each successful or rejected recovery is written to the durable Audit subsystem.

## Troubleshooting

- `404`: the record is absent or outside the operator scope.
- `409`: it is no longer dead-lettered; refresh before acting.
- `403`: the session lacks the required permission.
- `400 csrf_validation_failed`: refresh the page/session and retry through the UI.

Use `purpleglass.deadletter.requeued`, `purpleglass.deadletter.requeue.failed`, `purpleglass.deadletter.recovered.completed`, and `purpleglass.deadletter.recovered.failed`, plus `deadletter.*` spans. Repeated permanent failures require fixing the underlying cause before another controlled retry.

Known limitation: Task 7 supports one-message recovery only. It does not display/edit payloads, purge messages, bulk replay, or automatically recover failures.
