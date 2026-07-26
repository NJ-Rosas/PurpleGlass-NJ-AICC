# Telephony transport

PurpleGlass keeps telephone transport outside the CallManagement domain:

```text
Caller -> Twilio -> verified webhook adapter -> CallManagement
CallManagement -> durable telephony operation -> integrations worker
               -> ITelephonyProvider -> Twilio -> telephone network
```

CallManagement remains authoritative for the internal `CallSessionId`, tenant, location, lifecycle, correlation ID, outbox events, and realtime projection. `Provider`, `ProviderCallId`, and optional parent call identity are integration metadata. Provider SDK types exist only in `PurpleGlass.Adapters.Telephony.Twilio`.

## Configuration

Telephony is off by default and requires no credentials:

```text
Providers__EnableRealTelephony=false
Telephony__Provider=None
Telephony__PublicBaseUrl=
```

To opt into Twilio, set `Providers__EnableRealTelephony=true`, `Telephony__Provider=Twilio`, an HTTPS `Telephony__PublicBaseUrl`, and supply `Telephony__Twilio__AccountSid` and `Telephony__Twilio__AuthToken` through .NET user-secrets, environment injection, or a production secret manager. Never place credentials in JSON, `.env`, source control, logs, traces, or test output.

`PublicBaseUrl` must be the exact public origin Twilio calls. Signature validation includes the complete configured URL. A development tunnel may expose localhost, but no tunnel is a PurpleGlass runtime dependency.

The deterministic `Fake` adapter is available only in Development for automated/local transport exercises. It never calls a network. `None` always returns safe `provider_disabled` results.

## Number assignment

Authorized location administrators configure already-owned numbers through `PUT /bff/v1/telephony/numbers`. Storage is canonical E.164 (`+17875551234`). A provider/number pair is globally unique, and a location can have only one active outbound number. Inbound numbers may be tenant-wide, but an inbound call is accepted only when the number has an active location assignment and inbound use is enabled.

Number purchasing, porting, regulatory setup, CNAM, branded caller ID, and STIR/SHAKEN administration are out of scope.

## Webhooks and security

Twilio calls:

- `POST /telephony/twilio/inbound` to create or replay an inbound call and receive `<Connect><Stream>` TwiML after request-signature validation.
- `POST /telephony/twilio/answer` to reconcile the outbound provider identity and receive `<Connect><Stream>` TwiML after request-signature validation.
- `POST /telephony/twilio/status` for lifecycle changes.
- `WSS /telephony/twilio/media` for the signature-validated, call-scoped realtime media stream.

The HTTP routes and WebSocket upgrade use the official Twilio `RequestValidator`, require `X-Twilio-Signature`, validate required provider metadata, and have IP-partitioned rate/concurrency limits. Validation cannot be disabled by production configuration. Requests never choose a tenant: the normalized destination number or an existing provider identity resolves scope from persistence. The media adapter also validates Account SID, Call SID, Stream SID, protocol version, inbound track, and 8 kHz mono mu-law format before starting a voice session.

Browser mutations remain behind authenticated BFF sessions, permission policies, tenant/location checks, antiforgery validation, and per-user rate limits. Outbound and hangup requests are audited. Raw webhook bodies, signatures, credentials, and full phone numbers are not logged or used as metric dimensions.

## Lifecycle mapping

| Twilio status | PurpleGlass behavior |
| --- | --- |
| `queued`, `initiated` | keep `Requested`/`Received` |
| `ringing` | `Requested` -> `Ringing`; ignore when inbound or already later |
| `in-progress` | advance through legal transitions to `Answered` |
| `completed` | advance missing legal transitions when callbacks were skipped, then `Completed` |
| `busy` | `Failed` with `provider_busy` |
| `failed` | `Failed` with `provider_failed` |
| `no-answer` | `Failed` with `provider_no_answer` |
| `canceled` | `Failed` with `provider_canceled` |

Terminal calls ignore late callbacks. A receipt keyed by provider and stable callback event ID makes duplicate delivery idempotent. Twilio's callback idempotency header is preferred; the fallback is deterministic call-ID/status identity.

## Durable outbound and hangup behavior

The BFF transaction creates the call, idempotency receipt, outbox event, and `TelephonyOperation` together. The integrations worker commits `Dispatching` before contacting Twilio and persists the returned provider call ID afterward. The status URL includes the operation ID so a signed callback can reconcile provider identity even if it arrives before the REST response is committed.

Twilio's Calls create API does not document a client idempotency key. Therefore an operation left in `Dispatching` after an ambiguous crash is not blindly submitted again; this prevents duplicate real calls. A known safe rejection is categorized, the operation and call fail safely, and lifecycle/outbox events flow through the existing dead-letter-observable path. Automated replay of ambiguous submissions is intentionally deferred pending a provider reconciliation operation.

Hangup creates a separate durable, unique operation. The frontend never contacts Twilio directly. Final state comes from the signed lifecycle callback; repeated hangup requests are idempotent.

## Observability and correlation

Spans include `telephony.inbound.receive`, `telephony.outbound.start`, and `telephony.call.hangup`. Metrics cover inbound/outbound calls, connections, failures, callbacks, invalid signatures, and provider errors with only bounded provider/result dimensions.

A callback starts a new HTTP trace. PurpleGlass correlates it with durable call/provider identity and business correlation metadata; it does not claim the callback is a W3C child of the original request when Twilio did not propagate that context.

## Local verification

Run migrations and start normally with `Telephony:Provider=None`. The BFF and integrations worker must start without Twilio credentials. Automated tests use the fake adapter and synthetic numbers only. Live inbound/outbound validation is optional and must use externally supplied test credentials and test phone numbers.

Task 9 replaces the static transport message with a provider-neutral realtime STT -> LLM -> TTS pipeline. See [Realtime Voice Conversation Pipeline](./architecture/REALTIME_VOICE_PIPELINE.md). Recording, transfer, campaigns, SMS, dental tools, and multi-provider failover remain deferred.
