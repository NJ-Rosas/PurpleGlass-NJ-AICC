using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Collections.Concurrent;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;
using PurpleGlass.Observability;
using PurpleGlass.SharedKernel;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed class RealtimeVoiceSession(
    IRealtimeConversationPersistence conversations,
    ISpeechRecognizer speechRecognizer,
    IAiConversationRuntime languageModel,
    ISpeechSynthesizer speechSynthesizer,
    RealtimeVoiceOptions options,
    IVoiceSessionStateSink stateSink,
    TimeProvider timeProvider,
    IVoiceSessionDiagnostics diagnostics)
{
    private readonly object synchronization = new();
    private CancellationTokenSource? sessionSource;
    private CancellationTokenSource? activeOperation;
    private IRealtimeAudioTransport? transport;
    private VoiceSessionIdentity? identity;
    private Guid? conversationId;
    private VoiceSessionState state = VoiceSessionState.Connecting;
    private string stopReason = "media_disconnected";
    private string? activeResponseId;
    private long responseGeneration;
    private long activeResponseGeneration;
    private long activeResponseStartedTimestamp;
    private bool activeResponseCleared;
    private bool stopRequested;
    private int started;
    private readonly ConcurrentDictionary<Guid, long> endpointTimestamps = new();
    private CallLanguageState? languageState;

    public VoiceSessionState State
    {
        get { lock (synchronization) return state; }
    }

    public Guid CallId => identity?.CallId ?? Guid.Empty;

    public void RequestStop(string safeReason)
    {
        CancellationTokenSource? source;
        lock (synchronization)
        {
            stopReason = SafeCode(safeReason);
            stopRequested = true;
            source = sessionSource;
        }
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(
        VoiceSessionIdentity sessionIdentity,
        IRealtimeAudioTransport audioTransport,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("A realtime voice session instance can only run once.");
        options.Validate();
        ValidateIdentity(sessionIdentity, audioTransport);
        identity = sessionIdentity;
        transport = audioTransport;
        languageState = new CallLanguageState(
            sessionIdentity.StartingLanguageCode, sessionIdentity.StartingLanguageReason,
            timeProvider.GetUtcNow(), options.LanguagePolicy);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.Conversation.MaximumDuration);
        bool cancelImmediately;
        lock (synchronization)
        {
            sessionSource = source;
            cancelImmediately = stopRequested;
        }
        if (cancelImmediately) source.Cancel();
        CancellationToken sessionToken = source.Token;
        using Activity? sessionActivity = PurpleGlassTelemetry.Calls.StartActivity("voice.session", ActivityKind.Internal);
        sessionActivity?.SetTag("voice.provider", sessionIdentity.Provider);
        sessionActivity?.SetTag("voice.direction", sessionIdentity.Direction);
        sessionActivity?.SetTag("voice.language", ActiveLanguage);
        sessionActivity?.SetTag("voice.language_reason", languageState.Reason);
        PurpleGlassTelemetry.VoiceCallsByStartingLanguage.Add(1,
            new KeyValuePair<string, object?>("language", ActiveLanguage),
            new KeyValuePair<string, object?>("reason", languageState.Reason));
        PurpleGlassTelemetry.ActiveVoiceSessions.Add(1,
            new KeyValuePair<string, object?>("provider", sessionIdentity.Provider),
            new KeyValuePair<string, object?>("direction", sessionIdentity.Direction));

        Channel<RealtimeAudioFrame> audio = Channel.CreateBounded<RealtimeAudioFrame>(new BoundedChannelOptions(options.AudioQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Channel<FinalizedVoiceUtterance> utterances = Channel.CreateBounded<FinalizedVoiceUtterance>(new BoundedChannelOptions(options.UtteranceQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        bool failed = false;
        bool transportDisconnected = false;
        try
        {
            ConversationStatusProjection conversation = await EnsureConversationAsync(sessionIdentity, sessionToken);
            conversationId = conversation.ConversationId;
            languageState.Restore(conversation.Language, conversation.LanguageReason,
                conversation.LanguageChangedAtUtc, conversation.LanguageChangeSequence);
            await PublishStateAsync(VoiceSessionState.Connecting, null, sessionToken);
            conversation = await PersistGreetingAsync(conversation, sessionToken);

            Task receiveTask = ReceiveAudioAsync(audio.Writer, sessionToken);
            Task detectTask = DetectTurnsAsync(audio.Reader, utterances.Writer, sessionToken);
            Task processTask = ProcessTurnsAsync(utterances.Reader, sessionToken);
            Task greetingTask = SpeakGreetingAsync();
            Task startupCompleted = await Task.WhenAny(greetingTask, receiveTask, detectTask, processTask);
            if (!ReferenceEquals(startupCompleted, greetingTask))
            {
                transportDisconnected = receiveTask.IsCompletedSuccessfully;
                source.Cancel();
                await Task.WhenAll(receiveTask, detectTask, processTask, greetingTask);
            }
            await greetingTask;
            bool operationInProgress;
            lock (synchronization) operationInProgress = activeOperation is not null;
            if (!operationInProgress && State is not (VoiceSessionState.Interrupted or VoiceSessionState.Failed))
                await PublishStateAsync(VoiceSessionState.Listening, null, sessionToken);

            Task completed = await Task.WhenAny(receiveTask, detectTask, processTask);
            transportDisconnected = receiveTask.IsCompletedSuccessfully;
            source.Cancel();
            await Task.WhenAll(receiveTask, detectTask, processTask);
            string? completionFailure = await CompleteConversationAsync(stopReason, false);
            if (completionFailure is null)
                await PublishStateAsync(VoiceSessionState.Ended, null, CancellationToken.None);
            else
            {
                failed = true;
                RecordFailure(completionFailure);
                await PublishStateAsync(VoiceSessionState.Failed, completionFailure, CancellationToken.None);
            }

            async Task SpeakGreetingAsync()
            {
                try
                {
                    await SpeakAsync(Greeting, null, sessionToken);
                }
                catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
                {
                    // Caller barge-in cancels greeting playback, not the call-scoped session.
                }
                catch (VoicePipelineException exception)
                {
                    RecordFailure(exception.Code);
                    await PublishStateAsync(VoiceSessionState.Failed, exception.Code, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            bool explicitlyStopped;
            lock (synchronization) explicitlyStopped = stopRequested;
            if (!cancellationToken.IsCancellationRequested && !explicitlyStopped
                && !transportDisconnected && stopReason == "media_disconnected")
                stopReason = "maximum_duration";
            string? completionFailure = await CompleteConversationAsync(stopReason, false);
            if (completionFailure is null)
                await PublishStateAsync(VoiceSessionState.Ended, null, CancellationToken.None);
            else
            {
                failed = true;
                RecordFailure(completionFailure);
                await PublishStateAsync(VoiceSessionState.Failed, completionFailure, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed = true;
            string code = exception switch
            {
                VoicePipelineException pipeline => pipeline.Code,
                VoicePersistenceException persistence => persistence.Code,
                _ => "voice_session_failed",
            };
            ReportException(exception, "voice_session", code);
            sessionActivity?.SetStatus(ActivityStatusCode.Error, code);
            RecordFailure(code);
            await PublishStateAsync(VoiceSessionState.Failed, code, CancellationToken.None);
            _ = await CompleteConversationAsync(code, true);
        }
        finally
        {
            source.Cancel();
            audio.Writer.TryComplete();
            utterances.Writer.TryComplete();
            CancellationTokenSource? operation;
            lock (synchronization)
            {
                operation = activeOperation;
                activeOperation = null;
            }
            operation?.Cancel();
            operation?.Dispose();
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
            try { await audioTransport.CompleteAsync(failed ? "voice_session_failed" : stopReason, cleanup.Token); }
            catch (Exception) { }
            await audioTransport.DisposeAsync();
            lock (synchronization)
            {
                if (ReferenceEquals(sessionSource, source)) sessionSource = null;
            }
            source.Dispose();
            PurpleGlassTelemetry.ActiveVoiceSessions.Add(-1,
                new KeyValuePair<string, object?>("provider", sessionIdentity.Provider),
                new KeyValuePair<string, object?>("direction", sessionIdentity.Direction));
        }
    }

    private async Task<ConversationStatusProjection> EnsureConversationAsync(
        VoiceSessionIdentity sessionIdentity,
        CancellationToken cancellationToken)
    {
        ConversationStatusProjection conversation = await conversations.CreateAsync(new CreateConversation(
            sessionIdentity.TenantId, sessionIdentity.LocationId, sessionIdentity.CallId,
            sessionIdentity.CorrelationId, options.Conversation.Version,
            ActiveLanguage, TraceId: Activity.Current?.TraceId.ToString(),
            LanguageReason: languageState!.Reason), cancellationToken);
        if (conversation.State == "Created")
            conversation = await conversations.ActivateAsync(new ChangeConversationState(
                sessionIdentity.TenantId, conversation.ConversationId, conversation.Version,
                TraceId: Activity.Current?.TraceId.ToString()), cancellationToken);
        if (conversation.State != "Active")
            throw new VoicePipelineException("conversation_not_active", "The conversation is no longer active.");
        return conversation;
    }

    private async Task<ConversationStatusProjection> PersistGreetingAsync(
        ConversationStatusProjection conversation,
        CancellationToken cancellationToken)
    {
        Guid greetingId = DeterministicId(conversation.CallId, "voice:greeting");
        _ = await conversations.AddAssistantTurnAsync(new AddConversationTurn(
            identity!.TenantId, conversation.ConversationId, conversation.Version,
            greetingId, Greeting,
            TraceId: Activity.Current?.TraceId.ToString()), cancellationToken);
        return await conversations.GetAsync(identity.TenantId, conversation.ConversationId, cancellationToken);
    }

    private async Task ReceiveAudioAsync(ChannelWriter<RealtimeAudioFrame> writer, CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Calls.StartActivity("audio.receive", ActivityKind.Consumer);
        try
        {
            await foreach (RealtimeAudioFrame frame in transport!.ReceiveAsync(cancellationToken))
                await writer.WriteAsync(frame, cancellationToken);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DetectTurnsAsync(
        ChannelReader<RealtimeAudioFrame> reader,
        ChannelWriter<FinalizedVoiceUtterance> writer,
        CancellationToken cancellationToken)
    {
        using var detector = new RealtimeTurnDetector(options);
        try
        {
            await foreach (RealtimeAudioFrame frame in reader.ReadAllAsync(cancellationToken))
            {
                TurnDetectionResult result = detector.Push(frame);
                if (result.Duplicate) continue;
                if (result.CandidateStarted)
                    diagnostics.RecordInboundTurn(new VoiceInboundTurnDiagnostic(
                        identity!.CallId,
                        identity.CorrelationId,
                        $"candidate-{frame.Sequence}",
                        1,
                        DurationMs: 0,
                        QualifiedSpeechMs: 0,
                        NoiseFloor: detector.NoiseFloor,
                        EnergyMetric: 0,
                        SpeechQualified: false,
                        SttSubmitted: false,
                        DiscardReason: null,
                        Event: "candidate_started"));
                if (result.RejectedCandidate is not null)
                    diagnostics.RecordInboundTurn(new VoiceInboundTurnDiagnostic(
                        identity!.CallId,
                        identity.CorrelationId,
                        $"candidate-{result.RejectedCandidate.FirstSequence}-{result.RejectedCandidate.LastSequence}",
                        result.RejectedCandidate.InboundFrames,
                        result.RejectedCandidate.Duration.TotalMilliseconds,
                        result.RejectedCandidate.QualifiedSpeechDuration.TotalMilliseconds,
                        result.RejectedCandidate.NoiseFloor,
                        result.RejectedCandidate.EnergyMetric,
                        SpeechQualified: false,
                        SttSubmitted: false,
                        result.RejectedCandidate.DiscardReason,
                        Event: "candidate_rejected"));
                if (!detector.IsSpeechActive
                    && detector.SilenceDuration >= options.Conversation.InactivityTimeout)
                    throw new VoicePipelineException("maximum_silence", "The caller was inactive for too long.");
                if (result.SpeechStarted)
                {
                    diagnostics.RecordInboundTurn(new VoiceInboundTurnDiagnostic(
                        identity!.CallId,
                        identity.CorrelationId,
                        $"confirmed-{frame.Sequence}",
                        InboundFrames: 0,
                        DurationMs: options.MinimumSpeechDuration.TotalMilliseconds,
                        QualifiedSpeechMs: options.MinimumSpeechDuration.TotalMilliseconds,
                        NoiseFloor: detector.NoiseFloor,
                        EnergyMetric: 0,
                        SpeechQualified: true,
                        SttSubmitted: false,
                        DiscardReason: null,
                        Event: "speech_confirmed"));
                    await HandleSpeechStartedAsync(cancellationToken);
                }
                if (result.FinalizedUtterance is not null)
                {
                    long endpointTimestamp = timeProvider.GetTimestamp();
                    endpointTimestamps[result.FinalizedUtterance.TurnId] = endpointTimestamp;
                    double endpointDetectionMs = Math.Max(0,
                        (timeProvider.GetUtcNow() - (result.FinalizedUtterance.LastSpeechAtUtc
                            ?? result.FinalizedUtterance.EndedAtUtc)).TotalMilliseconds);
                    PurpleGlassTelemetry.VoiceEndpointLatency.Record(
                        endpointDetectionMs,
                        new KeyValuePair<string, object?>("provider", identity!.Provider));
                    diagnostics.RecordLatency(new VoiceLatencyDiagnostic(
                        identity.CallId,
                        ConversationId: null,
                        identity.CorrelationId,
                        result.FinalizedUtterance.TurnId.ToString("N"),
                        ResponseId: "none",
                        Stage: "final_speech_to_endpoint",
                        DurationMs: endpointDetectionMs,
                        ElapsedFromEndpointMs: 0,
                        Adapter: identity.Provider,
                        Result: "success"));
                    diagnostics.RecordInboundTurn(new VoiceInboundTurnDiagnostic(
                        identity!.CallId,
                        identity.CorrelationId,
                        result.FinalizedUtterance.TurnId.ToString("N"),
                        result.FinalizedUtterance.InboundFrames,
                        (result.FinalizedUtterance.Duration ?? TimeSpan.Zero).TotalMilliseconds,
                        (result.FinalizedUtterance.QualifiedSpeechDuration ?? TimeSpan.Zero).TotalMilliseconds,
                        result.FinalizedUtterance.NoiseFloor,
                        result.FinalizedUtterance.EnergyMetric,
                        SpeechQualified: true,
                        SttSubmitted: false,
                        DiscardReason: null,
                        Event: "utterance_finalized"));
                    await writer.WriteAsync(result.FinalizedUtterance, cancellationToken);
                }
            }

            FinalizedVoiceUtterance? final = detector.Flush();
            if (final is not null) await writer.WriteAsync(final, cancellationToken);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task HandleSpeechStartedAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? operation = null;
        bool interrupted;
        string responseId;
        long responseStartedTimestamp;
        lock (synchronization)
        {
            operation = activeOperation;
            interrupted = operation is not null;
            if (interrupted && activeResponseId is not null) activeResponseCleared = true;
            responseId = activeResponseId ?? "none";
            responseStartedTimestamp = activeResponseStartedTimestamp;
            if (interrupted) activeResponseGeneration = 0;
        }

        if (interrupted)
        {
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity!.CallId, identity.CorrelationId, responseId,
                "barge_in_requested", 0, 0, "confirmed_speech",
                ElapsedMs: responseStartedTimestamp == 0 ? 0
                    : timeProvider.GetElapsedTime(responseStartedTimestamp).TotalMilliseconds));
            try { operation?.Cancel(); }
            catch (ObjectDisposedException) { }
            try
            {
                diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                    identity.CallId, identity.CorrelationId, responseId,
                    "twilio_clear_send_started", 0, 0, "confirmed_speech",
                    ElapsedMs: responseStartedTimestamp == 0 ? 0
                        : timeProvider.GetElapsedTime(responseStartedTimestamp).TotalMilliseconds));
                await transport!.ClearPlaybackAsync(cancellationToken);
                diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                    identity.CallId, identity.CorrelationId, responseId,
                    "twilio_clear_send_completed", 0, 0, "confirmed_speech",
                    ElapsedMs: responseStartedTimestamp == 0 ? 0
                        : timeProvider.GetElapsedTime(responseStartedTimestamp).TotalMilliseconds));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { }
            PurpleGlassTelemetry.VoiceInterruptions.Add(1,
                new KeyValuePair<string, object?>("provider", identity!.Provider));
            await PublishStateAsync(VoiceSessionState.Interrupted, null, cancellationToken);
        }
        else if (State == VoiceSessionState.Failed)
        {
            await PublishStateAsync(VoiceSessionState.Listening, null, cancellationToken);
        }
    }

    private async Task ProcessTurnsAsync(
        ChannelReader<FinalizedVoiceUtterance> reader,
        CancellationToken cancellationToken)
    {
        int processedTurns = 0;
        var submittedTurnIds = new HashSet<Guid>();
        await foreach (FinalizedVoiceUtterance utterance in reader.ReadAllAsync(cancellationToken))
        {
            if (!submittedTurnIds.Add(utterance.TurnId)) continue;
            if (++processedTurns > options.Conversation.MaximumTurns)
                throw new VoicePipelineException("maximum_turns", "The conversation reached its configured turn limit.");
            await ProcessTurnAsync(utterance, cancellationToken);
        }
    }

    private async Task ProcessTurnAsync(FinalizedVoiceUtterance utterance, CancellationToken sessionToken)
    {
        VoiceSessionIdentity sessionIdentity = identity
            ?? throw new InvalidOperationException("Voice session identity is unavailable.");
        long startedAt = timeProvider.GetTimestamp();
        long endpointTimestamp = endpointTimestamps.TryRemove(utterance.TurnId, out long recordedEndpoint)
            ? recordedEndpoint : startedAt;
        var timing = new TurnLatency(utterance.TurnId, endpointTimestamp);
        using Activity? turnActivity = PurpleGlassTelemetry.Calls.StartActivity("voice.turn", ActivityKind.Internal);
        turnActivity?.SetTag("voice.provider", sessionIdentity.Provider);
        turnActivity?.SetTag("voice.language", ActiveLanguage);
        using CancellationTokenSource operation = BeginOperation(sessionToken);
        string result = "success";
        Guid? callerTurnId = null;
        ConversationStatusProjection? activeConversation = null;
        try
        {
            callerTurnId = DeterministicId(
                sessionIdentity.CallId, $"voice:caller:{utterance.TurnId:N}");
            diagnostics.RecordInboundTurn(new VoiceInboundTurnDiagnostic(
                sessionIdentity.CallId,
                sessionIdentity.CorrelationId,
                utterance.TurnId.ToString("N"),
                utterance.InboundFrames,
                (utterance.Duration ?? utterance.EndedAtUtc - utterance.StartedAtUtc).TotalMilliseconds,
                (utterance.QualifiedSpeechDuration ?? TimeSpan.Zero).TotalMilliseconds,
                utterance.NoiseFloor,
                utterance.EnergyMetric,
                SpeechQualified: true,
                SttSubmitted: true,
                DiscardReason: null,
                Event: "stt_submitted"));
            RuntimeInvocationContext context = RuntimeContext(callerTurnId.Value);
            SpeechRecognitionResult recognition = await RecognizeAsync(context, utterance, timing, operation.Token);
            if (recognition.Failure is not null)
                throw new VoicePipelineException(recognition.Failure.Code, recognition.Failure.SafeMessage);
            string recognizedText = recognition.RecognizedText.Trim();
            if (!recognition.IsFinal || recognizedText.Length == 0)
                throw new VoicePipelineException("speech_transcript_invalid", "The finalized transcript was empty.");

            ConversationStatusProjection conversation = await conversations.GetAsync(
                sessionIdentity.TenantId, conversationId!.Value, operation.Token);
            long callerPersistStarted = timeProvider.GetTimestamp();
            RecordLatency(timing, "caller_persistence_started", callerPersistStarted, callerPersistStarted,
                "persistence", "started");
            _ = await conversations.AddCallerTurnAsync(new AddConversationTurn(
                sessionIdentity.TenantId, conversation.ConversationId, conversation.Version,
                callerTurnId.Value, recognizedText, recognition.Confidence,
                CausationId: callerTurnId.Value, TraceId: Activity.Current?.TraceId.ToString(),
                StartedAtUtc: recognition.StartedAtUtc ?? utterance.StartedAtUtc,
                EndedAtUtc: recognition.EndedAtUtc ?? utterance.EndedAtUtc), sessionToken);
            long callerPersistEnded = timeProvider.GetTimestamp();
            RecordLatency(timing, "caller_persistence_completed", callerPersistStarted, callerPersistEnded,
                "persistence", "success");

            operation.Token.ThrowIfCancellationRequested();

            conversation = await conversations.GetAsync(sessionIdentity.TenantId, conversation.ConversationId, sessionToken);
            CallLanguageDecision languageDecision = languageState!.Evaluate(
                recognizedText, recognition, timeProvider.GetUtcNow());
            RecordLanguageDecision(languageDecision);
            if (languageDecision.Accepted)
            {
                Guid changeId = DeterministicId(sessionIdentity.CallId,
                    $"voice:language:{callerTurnId:N}:{languageDecision.Version}");
                conversation = await conversations.ChangeLanguageAsync(new ChangeConversationLanguage(
                    sessionIdentity.TenantId, conversation.ConversationId, conversation.Version,
                    changeId, languageDecision.ActiveLanguageCode, languageDecision.Reason,
                    languageDecision.Confidence, CausationId: utterance.TurnId,
                    TraceId: Activity.Current?.TraceId.ToString()), sessionToken);
                turnActivity?.SetTag("voice.language", ActiveLanguage);
            }
            activeConversation = conversation;
            IReadOnlyList<LiveTranscriptTurn> transcript = await conversations.GetTranscriptAsync(
                sessionIdentity.TenantId, conversation.ConversationId, sessionToken);
            await PublishStateAsync(VoiceSessionState.Thinking, null, operation.Token);
            string assistantText;
            if (languageDecision.Unsupported)
                assistantText = UnsupportedLanguageResponse;
            else
            {
                AiResponseResult response = await GenerateAsync(context, transcript, recognizedText, timing, operation.Token);
                if (response.Failure is not null)
                    throw new VoicePipelineException(response.Failure.Code, response.Failure.SafeMessage);
                assistantText = VoiceResponsePolicy.Constrain(
                    response.AssistantText, options.Conversation.MaximumResponseCharacters);
            }
            Guid assistantTurnId = DeterministicId(sessionIdentity.CallId, $"voice:assistant:{callerTurnId:N}");
            long assistantPersistStarted = timeProvider.GetTimestamp();
            RecordLatency(timing, "assistant_persistence_started", assistantPersistStarted, assistantPersistStarted,
                "persistence", "started");
            _ = await conversations.AddAssistantTurnAsync(new AddConversationTurn(
                sessionIdentity.TenantId, conversation.ConversationId, conversation.Version,
                assistantTurnId, assistantText,
                CausationId: utterance.TurnId, TraceId: Activity.Current?.TraceId.ToString()), operation.Token);
            long assistantPersistEnded = timeProvider.GetTimestamp();
            RecordLatency(timing, "assistant_persistence_completed", assistantPersistStarted, assistantPersistEnded,
                "persistence", "success");
            operation.Token.ThrowIfCancellationRequested();
            await SpeakAsync(assistantText, operation, operation.Token, timing);
            await PublishStateAsync(VoiceSessionState.Listening, null, operation.Token);
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            result = "interrupted";
            turnActivity?.SetStatus(ActivityStatusCode.Error, result);
        }
        catch (VoicePipelineException exception)
        {
            result = exception.Code;
            turnActivity?.SetStatus(ActivityStatusCode.Error, result);
            RecordFailure(result);
            await PublishStateAsync(VoiceSessionState.Failed, result, CancellationToken.None);
            if (!IsSynthesisFailure(result))
            {
                bool modelFailure = IsGenerationFailure(result)
                    && callerTurnId.HasValue && activeConversation is not null;
                if (modelFailure)
                    await TryPersistAndSpeakFallbackAsync(
                        activeConversation!, callerTurnId!.Value, utterance.TurnId, sessionToken);
                else
                    await TrySpeakFallbackAsync(sessionToken);
            }
            if (!sessionToken.IsCancellationRequested)
                await PublishStateAsync(VoiceSessionState.Listening, null, sessionToken);
        }
        finally
        {
            EndOperation(operation);
            PurpleGlassTelemetry.VoiceTurns.Add(1,
                new KeyValuePair<string, object?>("provider", sessionIdentity.Provider),
                new KeyValuePair<string, object?>("result", SafeCode(result)),
                new KeyValuePair<string, object?>("language", ActiveLanguage));
            PurpleGlassTelemetry.VoiceTurnDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", sessionIdentity.Provider),
                new KeyValuePair<string, object?>("result", SafeCode(result)),
                new KeyValuePair<string, object?>("language", ActiveLanguage));
            PurpleGlassTelemetry.VoiceTotalTurnLatency.Record(
                timeProvider.GetElapsedTime(endpointTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", sessionIdentity.Provider),
                new KeyValuePair<string, object?>("result", SafeCode(result)));
        }
    }

    private async Task<SpeechRecognitionResult> RecognizeAsync(
        RuntimeInvocationContext context,
        FinalizedVoiceUtterance utterance,
        TurnLatency timing,
        CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Calls.StartActivity("speech.recognize", ActivityKind.Client);
        PurpleGlassTelemetry.SpeechRecognitionRequests.Add(1);
        long startedAt = timeProvider.GetTimestamp();
        RecordLatency(timing, "stt_request_started", startedAt, startedAt,
            speechRecognizer.AdapterKey, "started");
        SpeechRecognitionResult result = await InvokeAsync(
            "recognize", options.RecognitionTimeout,
            token => speechRecognizer.RecognizeAsync(new SpeechRecognitionRequest(
                context, ActiveLanguage, new SimulatedUtteranceInput(string.Empty),
                new SpeechAudioInput(utterance.Format, utterance.Audio,
                    $"{identity!.ProviderMediaStreamId}:{utterance.FirstSequence}:{utterance.LastSequence}")), token),
            value => value.Failure, cancellationToken);
        long completedAt = timeProvider.GetTimestamp();
        PurpleGlassTelemetry.SpeechRecognitionDuration.Record(timeProvider.GetElapsedTime(startedAt, completedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("adapter", speechRecognizer.AdapterKey));
        RecordLatency(timing, "stt_completed", startedAt, completedAt,
            speechRecognizer.AdapterKey, result.Failure?.Code ?? "success");
        return result;
    }

    private async Task<AiResponseResult> GenerateAsync(
        RuntimeInvocationContext context,
        IReadOnlyList<LiveTranscriptTurn> transcript,
        string callerText,
        TurnLatency timing,
        CancellationToken cancellationToken)
    {
        using Activity? activity = PurpleGlassTelemetry.Calls.StartActivity("ai.generate", ActivityKind.Client);
        PurpleGlassTelemetry.AiRequests.Add(1);
        long startedAt = timeProvider.GetTimestamp();
        RecordLatency(timing, "llm_request_started", startedAt, startedAt,
            languageModel.AdapterKey, "started");
        SanitizedConversationTurn[] history = transcript
            .OrderBy(turn => turn.SequenceNumber)
            .TakeLast(options.Conversation.MaximumHistoryTurns)
            .Select(turn => new SanitizedConversationTurn(turn.Speaker, turn.Text))
            .ToArray();
        try
        {
            AiResponseResult result = await InvokeAsync(
                "generate", options.LanguageModelTimeout,
                token => languageModel.GenerateAsync(new AiResponseRequest(
                    context, ActiveConfiguration, DentalAgentBehavior.Build(ActiveConfiguration),
                    history, callerText, [],
                    new SafetyEscalationPolicy(options.Conversation.SafetyPolicyVersion,
                        options.Conversation.EscalationKeywords, options.Conversation.UrgentKeywords)), token),
                value => value.Failure, cancellationToken);
            double durationMs = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            RecordLatency(timing, "llm_completed", startedAt, timeProvider.GetTimestamp(),
                result.Provider ?? languageModel.AdapterKey, "success");
            PurpleGlassTelemetry.AiDuration.Record(durationMs,
                new KeyValuePair<string, object?>("adapter", languageModel.AdapterKey));
            diagnostics.RecordModelTurn(new ConversationModelTurnDiagnostic(
                context.ConversationId, context.CallId, context.TenantId, context.LocationId,
                result.Provider ?? languageModel.AdapterKey, result.Model ?? "configured",
                history.Length, durationMs, "success", false, result.ProviderRequestId,
                result.Usage.InputUnits, result.Usage.OutputUnits));
            return result;
        }
        catch (VoicePipelineException exception)
        {
            double durationMs = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            RecordLatency(timing, "llm_completed", startedAt, timeProvider.GetTimestamp(),
                languageModel.AdapterKey, SafeCode(exception.Code));
            PurpleGlassTelemetry.AiDuration.Record(durationMs,
                new KeyValuePair<string, object?>("adapter", languageModel.AdapterKey));
            diagnostics.RecordModelTurn(new ConversationModelTurnDiagnostic(
                context.ConversationId, context.CallId, context.TenantId, context.LocationId,
                languageModel.AdapterKey, "configured", history.Length, durationMs,
                SafeCode(exception.Code), true, null, 0, 0));
            throw;
        }
    }

    private async Task SpeakAsync(
        string text,
        CancellationTokenSource? existingOperation,
        CancellationToken cancellationToken,
        TurnLatency? timing = null)
    {
        CancellationTokenSource? ownedOperation = null;
        long generation = 0;
        long responseStarted = 0;
        string diagnosticResponseId = Guid.NewGuid().ToString("N");
        string synthesisLanguage = ActiveLanguage;
        if (existingOperation is null)
        {
            ownedOperation = BeginOperation(cancellationToken);
            existingOperation = ownedOperation;
        }
        try
        {
            generation = Interlocked.Increment(ref responseGeneration);
            responseStarted = timeProvider.GetTimestamp();
            lock (synchronization)
            {
                activeResponseId = diagnosticResponseId;
                activeResponseGeneration = generation;
                activeResponseStartedTimestamp = responseStarted;
                activeResponseCleared = false;
            }
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity!.CallId, identity.CorrelationId, diagnosticResponseId,
                "playback_preparing", 0, 0, "new_response"));
            await PublishStateAsync(VoiceSessionState.Speaking, null, existingOperation.Token);
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity.CallId, identity.CorrelationId, diagnosticResponseId,
                "tts_started", 0, 0, "response_created"));
            using Activity? synthActivity = PurpleGlassTelemetry.Calls.StartActivity("speech.synthesize", ActivityKind.Client);
            PurpleGlassTelemetry.SpeechSynthesisRequests.Add(1);
            long synthesisStarted = timeProvider.GetTimestamp();
            if (timing is not null)
                RecordLatency(timing, "tts_request_started", synthesisStarted, synthesisStarted,
                    speechSynthesizer.AdapterKey, "started", diagnosticResponseId);
            Task mediaReady = transport!.WaitForMediaReadyAsync(existingOperation.Token).AsTask();
            var stream = Channel.CreateBounded<SpeechSynthesisStreamUpdate>(new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            using var synthesisSource = CancellationTokenSource.CreateLinkedTokenSource(existingOperation.Token);
            synthesisSource.CancelAfter(options.SynthesisTimeout);
            SpeechSynthesisResult? synthesis = null;
            long firstAudioReceived = 0;
            long synthesisCompleted = 0;
            int finalChunks = 0;
            Task producer = ProduceSpeechAsync();

            using Activity? sendActivity = PurpleGlassTelemetry.Calls.StartActivity("audio.send", ActivityKind.Producer);
            RealtimeAudioSendResult sendResult = RealtimeAudioSendResult.Pending;
            bool mediaReadyObserved = false;
            bool firstMediaObserved = false;
            try
            {
                await foreach (SpeechSynthesisStreamUpdate update in stream.Reader.ReadAllAsync(existingOperation.Token))
                {
                    if (update.Completion is not null)
                    {
                        synthesis = update.Completion;
                        continue;
                    }
                    SynthesizedAudioChunk chunk = update.AudioChunk
                        ?? throw new VoicePipelineException("speech_synthesis_invalid", "The speech provider returned an invalid update.");
                    existingOperation.Token.ThrowIfCancellationRequested();
                    lock (synchronization)
                    {
                        if (activeResponseGeneration != generation)
                            throw new OperationCanceledException(existingOperation.Token);
                    }
                    if (!mediaReadyObserved)
                    {
                        await mediaReady;
                        mediaReadyObserved = true;
                        diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                            identity.CallId, identity.CorrelationId, diagnosticResponseId,
                            "media_ready", 0, 0, "provider_stream_started",
                            ElapsedMs: timeProvider.GetElapsedTime(responseStarted).TotalMilliseconds));
                    }
                    sendResult = await transport!.SendAsync(chunk, existingOperation.Token);
                    if (!string.IsNullOrWhiteSpace(sendResult.ResponseId))
                    {
                        lock (synchronization)
                        {
                            if (activeResponseGeneration == generation)
                                activeResponseId = sendResult.ResponseId;
                        }
                    }
                    if (!firstMediaObserved && sendResult.MediaMessageCount > 0)
                    {
                        firstMediaObserved = true;
                        long firstMedia = timeProvider.GetTimestamp();
                        double transportDelay = firstAudioReceived == 0 ? 0
                            : timeProvider.GetElapsedTime(firstAudioReceived, firstMedia).TotalMilliseconds;
                        PurpleGlassTelemetry.VoiceFirstAudioTransportDelay.Record(transportDelay,
                            new KeyValuePair<string, object?>("provider", identity.Provider));
                        if (timing is not null)
                        {
                            PurpleGlassTelemetry.VoiceEndpointToFirstMedia.Record(
                                timeProvider.GetElapsedTime(timing.EndpointTimestamp, firstMedia).TotalMilliseconds,
                                new KeyValuePair<string, object?>("provider", identity.Provider));
                            RecordLatency(timing, "first_twilio_media_sent", firstAudioReceived, firstMedia,
                                identity.Provider, "success", sendResult.ResponseId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!existingOperation.IsCancellationRequested
                && synthesisSource.IsCancellationRequested)
            {
                await producer;
                if (firstMediaObserved) await transport.ClearPlaybackAsync(existingOperation.Token);
                throw new VoicePipelineException("speech_synthesis_timeout", "Speech synthesis timed out.");
            }
            catch
            {
                synthesisSource.Cancel();
                await producer;
                throw;
            }
            await producer;
            if (synthesis is null)
                throw new VoicePipelineException("speech_synthesis_incomplete", "The speech provider returned incomplete audio.");
            if (synthesis.Failure is not null)
            {
                if (firstMediaObserved) await transport.ClearPlaybackAsync(existingOperation.Token);
                throw new VoicePipelineException(synthesis.Failure.Code, synthesis.Failure.SafeMessage);
            }
            if (finalChunks != 1 || !sendResult.MarkSent)
                throw new VoicePipelineException("speech_synthesis_incomplete", "The speech provider returned incomplete audio.");
            if (!sendResult.MarkSent || sendResult.MediaMessageCount == 0 || sendResult.MuLawBytes == 0)
                throw new VoicePipelineException(
                    "speech_synthesis_output_missing", "Synthesized speech produced no provider media.");
            diagnostics.RecordOutboundResponse(new VoiceOutboundResponseDiagnostic(
                identity!.CallId,
                identity.CorrelationId,
                sendResult.ResponseId,
                sendResult.SourcePcmBytes,
                sendResult.SourceSamples,
                sendResult.ResampledSamples,
                sendResult.MuLawBytes,
                sendResult.MediaMessageCount,
                sendResult.MarkSent,
                Cleared: false,
                Canceled: false));
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity.CallId, identity.CorrelationId, sendResult.ResponseId,
                "playback_mark_sent", sendResult.MediaMessageCount,
                sendResult.MaximumBufferedAudioDurationMs, "local_media_complete",
                ElapsedMs: timeProvider.GetElapsedTime(responseStarted).TotalMilliseconds));
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity.CallId, identity.CorrelationId, sendResult.ResponseId,
                "playback_pacing_summary", sendResult.MediaMessageCount,
                sendResult.MaximumBufferedAudioDurationMs, "response_complete",
                PacketDurationMs: sendResult.PacketDurationMs,
                StartupBufferedAudioDurationMs: sendResult.StartupBufferedAudioDurationMs,
                UnderflowCount: sendResult.UnderflowCount,
                AveragePacingLatenessMs: sendResult.AveragePacingLatenessMs,
                MaximumPacingLatenessMs: sendResult.MaximumPacingLatenessMs,
                SchedulerLateCount: sendResult.SchedulerLateCount,
                ProducerStarvationCount: sendResult.ProducerStarvationCount,
                RemoteBufferUnderflowCount: sendResult.RemoteBufferUnderflowCount,
                RebufferCount: sendResult.RebufferCount,
                TotalRebufferMs: sendResult.TotalRebufferMs,
                MinimumEstimatedRemoteReserveMs: sendResult.MinimumEstimatedRemoteReserveMs,
                MaximumEstimatedRemoteReserveMs: sendResult.MaximumEstimatedRemoteReserveMs,
                MaximumSendDurationMs: sendResult.MaximumSendDurationMs,
                StartupTargetMs: sendResult.StartupTargetMs,
                LowWaterThresholdMs: sendResult.LowWaterThresholdMs,
                HighWaterTargetMs: sendResult.HighWaterTargetMs,
                MaximumSendAheadMs: sendResult.MaximumSendAheadMs,
                ProactiveRefillCount: sendResult.ProactiveRefillCount,
                AverageSendDurationMs: sendResult.AverageSendDurationMs));
            lock (synchronization)
            {
                if (activeResponseGeneration == generation)
                    activeResponseId = sendResult.ResponseId;
            }
            RealtimePlaybackCompletion playback = await transport!.WaitForPlaybackCompletionAsync(
                sendResult.ResponseId, existingOperation.Token);
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity.CallId, identity.CorrelationId, playback.ResponseId,
                playback.MarkAcknowledged ? "playback_mark_acknowledged" : "playback_cleared",
                sendResult.MediaMessageCount, sendResult.MaximumBufferedAudioDurationMs,
                playback.MarkAcknowledged ? "provider_playback_complete" : "barge_in",
                ElapsedMs: timeProvider.GetElapsedTime(responseStarted).TotalMilliseconds,
                ElapsedFromFirstMediaMs: playback.ElapsedFromFirstMediaMs,
                ElapsedFromMarkSentMs: playback.ElapsedFromMarkSentMs));

            async Task ProduceSpeechAsync()
            {
                try
                {
                    var request = new SpeechSynthesisRequest(
                        RuntimeContext(Guid.NewGuid()), text, synthesisLanguage,
                        new VoiceConfiguration(options.Conversation.VoiceId,
                            SpeakingRate: options.Conversation.SpeakingRate));
                    await foreach (SpeechSynthesisStreamUpdate update in speechSynthesizer
                        .SynthesizeStreamingAsync(request, synthesisSource.Token))
                    {
                        if (update.AudioChunk is not null)
                        {
                            if (update.AudioChunk.IsFinal) finalChunks++;
                            if (update.AudioChunk.Audio.Length > 0 && firstAudioReceived == 0)
                            {
                                firstAudioReceived = timeProvider.GetTimestamp();
                                double firstAudioMs = timeProvider.GetElapsedTime(
                                    synthesisStarted, firstAudioReceived).TotalMilliseconds;
                                PurpleGlassTelemetry.VoiceTtsFirstAudio.Record(firstAudioMs,
                                    new KeyValuePair<string, object?>("adapter", speechSynthesizer.AdapterKey));
                                diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                                    identity.CallId, identity.CorrelationId, diagnosticResponseId,
                                    "tts_first_audio", 0, 0, "provider_delta",
                                    ElapsedMs: timeProvider.GetElapsedTime(responseStarted).TotalMilliseconds));
                                if (timing is not null)
                                    RecordLatency(timing, "tts_first_audio", synthesisStarted, firstAudioReceived,
                                        speechSynthesizer.AdapterKey, "success", diagnosticResponseId);
                            }
                        }
                        if (update.Completion is not null)
                        {
                            synthesisCompleted = timeProvider.GetTimestamp();
                            double durationMs = timeProvider.GetElapsedTime(
                                synthesisStarted, synthesisCompleted).TotalMilliseconds;
                            PurpleGlassTelemetry.SpeechSynthesisDuration.Record(durationMs,
                                new KeyValuePair<string, object?>("adapter", speechSynthesizer.AdapterKey));
                            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                                identity.CallId, identity.CorrelationId, diagnosticResponseId,
                                update.Completion.Failure is null ? "tts_completed" : "tts_failed",
                                0, 0, update.Completion.Failure?.Code ?? "provider_completed",
                                ElapsedMs: timeProvider.GetElapsedTime(responseStarted).TotalMilliseconds));
                            if (timing is not null)
                                RecordLatency(timing, "tts_completed", synthesisStarted, synthesisCompleted,
                                    speechSynthesizer.AdapterKey,
                                    update.Completion.Failure?.Code ?? "success", diagnosticResponseId);
                        }
                        await stream.Writer.WriteAsync(update, synthesisSource.Token);
                    }
                    stream.Writer.TryComplete();
                }
                catch (Exception exception)
                {
                    stream.Writer.TryComplete(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            bool cleared;
            string responseId;
            lock (synchronization)
            {
                cleared = activeResponseCleared;
                responseId = activeResponseId ?? "response-canceled";
            }
            diagnostics.RecordOutboundResponse(new VoiceOutboundResponseDiagnostic(
                identity!.CallId, identity.CorrelationId, responseId,
                0, 0, 0, 0, 0, false, cleared, Canceled: true));
            diagnostics.RecordPlaybackEvent(new VoicePlaybackEventDiagnostic(
                identity.CallId, identity.CorrelationId, responseId,
                "playback_generation_canceled", 0, 0,
                cleared ? "barge_in" : "session_canceled"));
            throw;
        }
        finally
        {
            lock (synchronization)
            {
                if (activeResponseGeneration == generation
                    || string.Equals(activeResponseId, diagnosticResponseId, StringComparison.Ordinal))
                {
                    activeResponseGeneration = 0;
                    activeResponseStartedTimestamp = 0;
                    activeResponseId = null;
                    activeResponseCleared = false;
                }
            }
            if (ownedOperation is not null)
            {
                EndOperation(ownedOperation);
                ownedOperation.Dispose();
            }
        }
    }

    private async Task TrySpeakFallbackAsync(CancellationToken cancellationToken)
    {
        try { await SpeakAsync(FallbackResponse, null, cancellationToken); }
        catch (VoicePipelineException) { }
    }

    private async Task TryPersistAndSpeakFallbackAsync(
        ConversationStatusProjection conversation,
        Guid callerTurnId,
        Guid causationId,
        CancellationToken cancellationToken)
    {
        try
        {
            Guid assistantTurnId = DeterministicId(identity!.CallId, $"voice:assistant:{callerTurnId:N}");
            _ = await conversations.AddAssistantTurnAsync(new AddConversationTurn(
                identity.TenantId, conversation.ConversationId, conversation.Version,
                assistantTurnId, FallbackResponse,
                CausationId: causationId, TraceId: Activity.Current?.TraceId.ToString()), cancellationToken);
            await SpeakAsync(FallbackResponse, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VoicePipelineException)
        {
            // Fallback is best-effort; the original bounded model failure remains observable.
        }
        catch (VoicePersistenceException)
        {
            // Fallback is best-effort; the original bounded model failure remains observable.
        }
    }

    private static bool IsGenerationFailure(string code) =>
        code.StartsWith("ai_", StringComparison.Ordinal)
        || code.StartsWith("generate_", StringComparison.Ordinal);

    private async Task<T> InvokeAsync<T>(
        string operation,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> invoke,
        Func<T, RuntimeFailure?> failure,
        CancellationToken cancellationToken)
    {
        RuntimeFailure? lastFailure = null;
        for (int attempt = 1; attempt <= options.MaximumProviderAttempts; attempt++)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                T result = await invoke(timeoutSource.Token);
                lastFailure = failure(result);
                if (lastFailure is null) return result;
                if (!lastFailure.Retryable || attempt == options.MaximumProviderAttempts)
                    throw new VoicePipelineException(lastFailure.Code, lastFailure.SafeMessage);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == options.MaximumProviderAttempts)
                    throw new VoicePipelineException($"{operation}_timeout", $"The {operation} provider timed out.", exception);
            }
        }
        throw new VoicePipelineException(lastFailure?.Code ?? $"{operation}_failed",
            lastFailure?.SafeMessage ?? $"The {operation} provider failed.");
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (synchronization)
        {
            activeOperation?.Cancel();
            activeOperation = source;
        }
        return source;
    }

    private void EndOperation(CancellationTokenSource source)
    {
        lock (synchronization)
        {
            if (ReferenceEquals(activeOperation, source)) activeOperation = null;
        }
    }

    private async Task PublishStateAsync(
        VoiceSessionState next,
        string? safeCode,
        CancellationToken cancellationToken)
    {
        lock (synchronization) state = next;
        await stateSink.PublishAsync(new VoiceSessionStateChange(
            identity!, conversationId, next, safeCode is null ? null : SafeCode(safeCode),
            timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task<string?> CompleteConversationAsync(string outcome, bool fail)
    {
        if (!conversationId.HasValue || identity is null) return null;
        using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
        try
        {
            ConversationStatusProjection current = await conversations.GetAsync(identity.TenantId, conversationId.Value, cleanup.Token);
            if (current.State is "Completed" or "Failed") return null;
            if (fail)
            {
                _ = await conversations.FailAsync(new ChangeConversationState(
                    identity.TenantId, current.ConversationId, current.Version,
                    TraceId: Activity.Current?.TraceId.ToString()), cleanup.Token);
                return null;
            }
            IReadOnlyList<LiveTranscriptTurn> transcript = await conversations.GetTranscriptAsync(
                identity.TenantId, current.ConversationId, cleanup.Token);
            var summary = new ConversationSummary(
                $"Voice conversation ended with {transcript.Count} finalized transcript turns.",
                null, SafeCode(outcome), current.Escalated, current.Escalated,
                timeProvider.GetUtcNow(), options.Conversation.Version);
            _ = await conversations.CompleteAsync(new CompleteConversation(
                identity.TenantId, current.ConversationId, current.Version, summary,
                TraceId: Activity.Current?.TraceId.ToString()), cleanup.Token);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReportException(exception, fail ? "conversation_fail" : "conversation_complete",
                "voice_persistence_failed");
        }
        catch (OperationCanceledException exception)
        {
            return ReportException(exception, fail ? "conversation_fail" : "conversation_complete",
                "voice_persistence_failed");
        }
    }

    private string ReportException(Exception exception, string fallbackStage, string fallbackCode)
    {
        string code = exception is VoicePersistenceException persistence
            ? persistence.Code : fallbackCode;
        string stage = exception is VoicePersistenceException persistenceFailure
            ? persistenceFailure.Stage : fallbackStage;
        VoiceSessionIdentity sessionIdentity = identity
            ?? throw new InvalidOperationException("Voice session identity is unavailable.");
        diagnostics.RecordException(new VoiceSessionExceptionDiagnostic(
            sessionIdentity.CallId, conversationId,
            sessionIdentity.TenantId, sessionIdentity.LocationId,
            sessionIdentity.Provider, sessionIdentity.ProviderCallId,
            sessionIdentity.CorrelationId, stage, code,
            exception.GetType().Name, exception.GetBaseException().GetType().Name));
        return code;
    }

    private RuntimeInvocationContext RuntimeContext(Guid causationId) => new(
        identity!.TenantId, identity.LocationId, identity.CallId, conversationId!.Value,
        identity.CorrelationId, causationId, Activity.Current?.TraceId.ToString());

    private void RecordLatency(
        TurnLatency timing,
        string stage,
        long startedTimestamp,
        long completedTimestamp,
        string adapter,
        string result,
        string responseId = "none")
    {
        double durationMs = startedTimestamp == 0 || completedTimestamp == 0
            ? 0 : timeProvider.GetElapsedTime(startedTimestamp, completedTimestamp).TotalMilliseconds;
        double elapsedFromEndpointMs = completedTimestamp == 0
            ? 0 : timeProvider.GetElapsedTime(timing.EndpointTimestamp, completedTimestamp).TotalMilliseconds;
        diagnostics.RecordLatency(new VoiceLatencyDiagnostic(
            identity!.CallId,
            conversationId,
            identity.CorrelationId,
            timing.TurnId.ToString("N"),
            responseId,
            stage,
            Math.Max(0, durationMs),
            Math.Max(0, elapsedFromEndpointMs),
            SafeCode(adapter),
            SafeCode(result)));
    }

    private void RecordFailure(string code)
    {
        PurpleGlassTelemetry.VoiceFailures.Add(1,
            new KeyValuePair<string, object?>("provider", identity!.Provider),
            new KeyValuePair<string, object?>("result", SafeCode(code)),
            new KeyValuePair<string, object?>("language", ActiveLanguage));
        if (IsSynthesisFailure(code))
            PurpleGlassTelemetry.SpeechSynthesisFailures.Add(1);
        else if (code.StartsWith("speech_", StringComparison.Ordinal) || code.StartsWith("recognize_", StringComparison.Ordinal))
            PurpleGlassTelemetry.SpeechRecognitionFailures.Add(1);
        else if (code.StartsWith("ai_", StringComparison.Ordinal) || code.StartsWith("generate_", StringComparison.Ordinal))
            PurpleGlassTelemetry.AiFailures.Add(1);
    }

    private static bool IsSynthesisFailure(string code) =>
        code.StartsWith("synthesize_", StringComparison.Ordinal)
        || code.StartsWith("speech_synthesis_", StringComparison.Ordinal)
        || code.StartsWith("voice_", StringComparison.Ordinal);

    private string ActiveLanguage => languageState?.ActiveLanguageCode
        ?? SupportedCallLanguages.SystemFallbackCode;

    private ConversationRuntimeConfiguration ActiveConfiguration => options.Conversation with
    {
        Language = ActiveLanguage,
        Greeting = Greeting,
    };

    private string Greeting
    {
        get
        {
            if (SupportedCallLanguages.TryNormalize(options.Conversation.Language, out SupportedCallLanguage configured)
                && configured.Code.Equals(ActiveLanguage, StringComparison.OrdinalIgnoreCase))
                return options.Conversation.Greeting;
            return SupportedCallLanguages.TryNormalize(ActiveLanguage, out SupportedCallLanguage active)
                ? active.Greeting : SupportedCallLanguages.Fallback.Greeting;
        }
    }

    private string FallbackResponse => ActiveLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase)
        ? "Lo siento, tengo problemas para responder en este momento."
        : "I'm sorry, I'm having trouble responding right now.";

    private string UnsupportedLanguageResponse => ActiveLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase)
        ? "Actualmente puedo continuar en inglés o español."
        : "I can currently continue in English or Spanish.";

    private void RecordLanguageDecision(CallLanguageDecision decision)
    {
        string confidenceBucket = decision.Confidence switch
        {
            >= 0.90m => "high",
            >= 0.75m => "medium",
            not null => "low",
            _ => "not_provided",
        };
        if (decision.Accepted && decision.Reason == CallLanguageReasons.CallerExplicitRequest)
            PurpleGlassTelemetry.VoiceExplicitLanguageSwitches.Add(1,
                new KeyValuePair<string, object?>("language", decision.ActiveLanguageCode));
        else if (decision.Accepted && decision.Reason == CallLanguageReasons.AutomaticDetection)
            PurpleGlassTelemetry.VoiceAutomaticLanguageSwitches.Add(1,
                new KeyValuePair<string, object?>("language", decision.ActiveLanguageCode));
        else if (decision.Unsupported)
            PurpleGlassTelemetry.VoiceUnsupportedLanguageRequests.Add(1,
                new KeyValuePair<string, object?>("language", decision.ActiveLanguageCode));
        else if (decision.Result is "low_confidence" or "mixed_language" or "cooldown" or "evidence_accumulating")
            PurpleGlassTelemetry.VoiceLanguageSwitchesRejected.Add(1,
                new KeyValuePair<string, object?>("result", decision.Result));

        diagnostics.RecordLanguage(new VoiceLanguageDiagnostic(
            identity!.CallId, identity.CorrelationId, languageState!.StartingLanguageCode,
            decision.ActiveLanguageCode, decision.Reason, decision.Result, confidenceBucket,
            decision.AlternateEvidenceCount, decision.Accepted, decision.Unsupported, decision.Version));
    }

    private static void ValidateIdentity(VoiceSessionIdentity identity, IRealtimeAudioTransport transport)
    {
        if (identity.TenantId == Guid.Empty || identity.LocationId == Guid.Empty || identity.CallId == Guid.Empty
            || identity.CorrelationId == Guid.Empty)
            throw new ArgumentException("Internal voice session identity is incomplete.", nameof(identity));
        if (!string.Equals(identity.Provider, transport.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(identity.ProviderCallId, transport.ProviderCallId, StringComparison.Ordinal)
            || !string.Equals(identity.ProviderMediaStreamId, transport.ProviderMediaStreamId, StringComparison.Ordinal))
            throw new ArgumentException("Audio transport identity does not match the authorized call.", nameof(transport));
    }

    private static string SafeCode(string value)
    {
        string normalized = new(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')
            .Take(80).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "voice_session_ended" : normalized;
    }

    private sealed record TurnLatency(Guid TurnId, long EndpointTimestamp);

    private static Guid DeterministicId(Guid namespaceId, string value)
    {
        byte[] material = Encoding.UTF8.GetBytes($"{namespaceId:N}:{value}");
        return new Guid(SHA256.HashData(material)[..16]);
    }
}

public sealed class VoicePipelineException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
