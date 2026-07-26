# ADR 0010: Realtime voice conversation pipeline

- Status: Accepted
- Date: 2026-07-25

## Context

Task 8 connected PurpleGlass to a carrier but returned static TwiML. The existing simulator proved provider-neutral speech and AI contracts only for finite synthetic call runs. A real repeated-turn call needs a long-lived media connection, audio conversion, bounded backpressure, per-call cancellation, sequential turn processing, interruption, durable finalized transcripts, and safe operation without external credentials.

Putting Twilio JSON or OpenAI HTTP types in CallManagement or Conversation domain code would couple internal identity, lifecycle, persistence, and tests to vendors. A separate realtime host would add deployment and launcher complexity before the prototype has distributed session routing.

## Decision

The Web BFF owns signature-validated Twilio media WebSockets for Task 9. It already owns the public verified Twilio endpoints and can resolve the carrier Call SID through CallManagement before starting a session. No new host or launcher component is added.

Conversation application code defines `IRealtimeAudioTransport`, `ISpeechRecognizer`, `IAiConversationRuntime`, and `ISpeechSynthesizer`. Twilio and OpenAI implementations remain in adapter projects. Deterministic fake and disabled implementations keep local startup and automated tests credential-free.

`CallSessionId`, provider call ID, provider media stream ID, and `ConversationId` remain separate. The signed provider connection supplies carrier identities, but tenant/location scope is derived only from the stored call. Media connection establishes the legal CallManagement connected/in-conversation state; it does not create a parallel lifecycle.

Each active call owns one `RealtimeVoiceSession`, linked cancellation tree, bounded audio channel, bounded finalized-utterance channel, and single turn consumer. Duplicate frames are suppressed. PCM energy, explicit endpoint events, configurable silence, and maximum utterance duration determine finalization. Only finalized Caller and Assistant turns use the existing durable Conversation model.

Turns execute sequentially: STT, Caller persistence, bounded recent history, LLM, constrained Assistant persistence, TTS, and audio send. The predefined greeting and safe system prompt make no dental or tool-execution claims. New caller speech cancels the active LLM/TTS/send operation and clears carrier playback before processing the next turn.

External operations have configured short timeouts. At most two immediate attempts are allowed only for safe retryable results; one is the default. Live work never uses delayed outbox retry semantics. A turn failure emits a bounded code and attempts a generic spoken fallback when synthesis is available. Fatal session/media failures cancel and clean up the runtime.

Final transcript events continue through the existing transaction, outbox, MQTT, BFF SSE, and authorized call-detail query. Transient voice states use the existing BFF SSE connection and are not persisted. No second transcript model, event bus, browser store, or browser WebSocket is introduced.

Voice traces use `voice.session`, `voice.turn`, `speech.recognize`, `ai.generate`, `speech.synthesize`, `audio.receive`, and `audio.send`. Metrics use only bounded provider/result/direction/language/adapter dimensions. Logs and telemetry exclude audio, caller speech, transcript text, prompts, responses, phone numbers, credentials, provider tokens, and raw provider payloads.

## Consequences

- CallManagement and Conversation remain provider-neutral and retain authoritative identity, lifecycle, tenant scope, and persistence.
- Twilio mu-law/base64 framing and PCM conversion stay in the telephony adapter.
- OpenAI can be replaced independently for STT, LLM, or TTS.
- Fake and disabled configurations start without network access or credentials.
- Bounded queues, sequential turns, linked cancellation, and playback clear provide explicit backpressure and basic barge-in.
- The existing launcher remains sufficient because the Web BFF owns media sessions.
- Active runtime state is single-instance. Multi-instance deployment needs connection affinity or distributed session routing.
- Sessions cannot resume or migrate after a Web BFF restart; durable transcripts survive, active audio does not.
- Initial OpenAI STT uses finalized utterances rather than partial streaming, and TTS chunking begins after the provider response.
- Task 10 dental tools, patient data, appointment actions, RAG, and external clinical integrations remain out of scope.

## Alternatives considered

### Dedicated voice worker now

Rejected for Task 9. It would require another public endpoint, process, health surface, launcher component, and routing model without yet solving distributed session ownership. The neutral ports allow this move later.

### Put media processing in the integrations worker

Rejected. The integrations worker owns durable background dispatch and MQTT publication, not caller-facing long-lived connections with sub-second cancellation requirements.

### Couple the conversation engine directly to Twilio or OpenAI

Rejected. It would leak provider identity, codecs, HTTP payloads, and SDK choices into core application/domain code and make deterministic network-free tests difficult.

### Persist transient audio and operational state

Rejected. Raw audio, partial transcripts, and momentary Listening/Thinking/Speaking state are not required for recovery and would increase sensitive-data exposure and write volume.

## Related documentation

- [Realtime Voice Conversation Pipeline](../architecture/REALTIME_VOICE_PIPELINE.md)
- [Provider-neutral telephony boundary](0009-provider-neutral-telephony-boundary.md)
- [OpenTelemetry observability boundary](0007-open-telemetry-observability-boundary.md)
