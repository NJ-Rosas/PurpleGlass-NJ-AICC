using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.WebBff;

public sealed partial class VoiceSessionManager(
    IServiceScopeFactory scopeFactory,
    RealtimeVoiceOptions voiceOptions,
    ILogger<VoiceSessionManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionRegistration> sessions = new();

    public int ActiveCount => sessions.Count;

    public async Task RunAsync(
        VoiceCallContext call,
        IRealtimeAudioTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(transport);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RealtimeVoiceSession session = scope.ServiceProvider.GetRequiredService<RealtimeVoiceSession>();
        var registration = new SessionRegistration(session, transport.ProviderMediaStreamId);
        if (!sessions.TryAdd(call.CallId, registration))
        {
            using var cleanup = new CancellationTokenSource(voiceOptions.CleanupTimeout);
            try
            {
                await transport.CompleteAsync("voice_session_already_active", cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }
            finally
            {
                await transport.DisposeAsync();
            }
            throw new VoiceSessionConflictException("voice_session_already_active");
        }

        LogSessionStarted(logger, call.CallId, call.TenantId, call.LocationId,
            call.Provider, call.ProviderCallId, call.Direction, call.CorrelationId);
        try
        {
            await session.RunAsync(new VoiceSessionIdentity(
                call.TenantId, call.LocationId, call.CallId, call.Direction,
                call.Provider, call.ProviderCallId, transport.ProviderMediaStreamId,
                call.CorrelationId, call.StartingLanguageCode, call.StartingLanguageReason),
                transport, cancellationToken);
        }
        finally
        {
            _ = sessions.TryRemove(new KeyValuePair<Guid, SessionRegistration>(call.CallId, registration));
            LogSessionEnded(logger, call.CallId, call.Provider, session.State.ToString());
        }
    }

    public bool RequestStop(Guid callId, string safeReason)
    {
        if (!sessions.TryGetValue(callId, out SessionRegistration? registration)) return false;
        registration.Session.RequestStop(safeReason);
        LogSessionStopRequested(logger, callId, safeReason);
        return true;
    }

    public VoiceSessionState? GetState(Guid callId) =>
        sessions.TryGetValue(callId, out SessionRegistration? registration)
            ? registration.Session.State : null;

    public ValueTask DisposeAsync()
    {
        foreach ((Guid callId, SessionRegistration registration) in sessions)
        {
            registration.Session.RequestStop("application_stopping");
            LogSessionStopRequested(logger, callId, "application_stopping");
        }
        return ValueTask.CompletedTask;
    }

    private sealed record SessionRegistration(RealtimeVoiceSession Session, string ProviderMediaStreamId);

    [LoggerMessage(200, LogLevel.Information,
        "Voice session started; CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, ProviderCallId={ProviderCallId}, Direction={Direction}, CorrelationId={CorrelationId}.")]
    private static partial void LogSessionStarted(ILogger logger, Guid callId, Guid tenantId,
        Guid locationId, string provider, string providerCallId, string direction, Guid correlationId);

    [LoggerMessage(201, LogLevel.Information,
        "Voice session ended; CallId={CallId}, Provider={Provider}, State={State}.")]
    private static partial void LogSessionEnded(ILogger logger, Guid callId, string provider, string state);

    [LoggerMessage(202, LogLevel.Information,
        "Voice session stop requested; CallId={CallId}, Reason={Reason}.")]
    private static partial void LogSessionStopRequested(ILogger logger, Guid callId, string reason);
}

public sealed partial class BffVoiceSessionStateSink(
    RealtimeEventHub realtime,
    ILogger<BffVoiceSessionStateSink> logger) : IVoiceSessionStateSink
{
    public ValueTask PublishAsync(VoiceSessionStateChange change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (change.State == VoiceSessionState.Ended) return ValueTask.CompletedTask;
        if (change.State == VoiceSessionState.Failed)
            LogVoiceFailure(logger, change.Identity.CallId, change.Identity.TenantId,
                change.Identity.LocationId, change.Identity.Provider,
                change.Identity.ProviderCallId, change.Identity.CorrelationId,
                change.SafeCode ?? "voice_session_failed");
        string payload = JsonSerializer.Serialize(new
        {
            callId = change.Identity.CallId,
            state = change.State.ToString(),
            safeCode = change.SafeCode,
        });
        realtime.Publish(new RealtimeEvent(
            change.Identity.TenantId,
            change.Identity.LocationId,
            change.Identity.CorrelationId,
            Guid.NewGuid(),
            "voice-state-changed",
            payload,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString));
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(203, LogLevel.Warning,
        "Voice session failed; CallId={CallId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, ProviderCallId={ProviderCallId}, CorrelationId={CorrelationId}, SafeCode={SafeCode}.")]
    private static partial void LogVoiceFailure(ILogger logger, Guid callId, Guid tenantId,
        Guid locationId, string provider, string providerCallId, Guid correlationId, string safeCode);
}

public sealed partial class BffVoiceSessionDiagnostics(ILogger<BffVoiceSessionDiagnostics> logger)
    : IVoiceSessionDiagnostics
{
    public void RecordException(VoiceSessionExceptionDiagnostic diagnostic) =>
        LogSessionException(logger, diagnostic.CallId, diagnostic.ConversationId,
            diagnostic.TenantId, diagnostic.LocationId, diagnostic.Provider,
            diagnostic.ProviderCallId, diagnostic.CorrelationId, diagnostic.Stage,
            diagnostic.SafeCode, diagnostic.ExceptionType, diagnostic.RootExceptionType);

    public void RecordModelTurn(ConversationModelTurnDiagnostic diagnostic) =>
        LogModelTurn(logger, diagnostic.ConversationId, diagnostic.CallSessionId,
            diagnostic.TenantId, diagnostic.LocationId, diagnostic.Provider, diagnostic.Model,
            diagnostic.HistoryTurnCount, diagnostic.GenerationDurationMs,
            diagnostic.ResultCategory, diagnostic.FallbackUsed,
            diagnostic.ProviderRequestId ?? "none", diagnostic.InputTokenCount,
            diagnostic.OutputTokenCount);

    public void RecordInboundTurn(VoiceInboundTurnDiagnostic diagnostic) =>
        LogInboundTurn(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.TurnId, diagnostic.InboundFrames, diagnostic.DurationMs,
            diagnostic.QualifiedSpeechMs, diagnostic.NoiseFloor, diagnostic.EnergyMetric,
            diagnostic.SpeechQualified, diagnostic.SttSubmitted,
            diagnostic.DiscardReason ?? "none", diagnostic.Event);

    public void RecordOutboundResponse(VoiceOutboundResponseDiagnostic diagnostic) =>
        LogOutboundResponse(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.ResponseId, diagnostic.SourcePcmBytes, diagnostic.SourceSamples,
            diagnostic.ResampledSamples, diagnostic.MuLawBytes, diagnostic.MediaMessageCount,
            diagnostic.MarkSent, diagnostic.Cleared, diagnostic.Canceled);

    public void RecordPlaybackEvent(VoicePlaybackEventDiagnostic diagnostic) =>
        LogPlaybackEvent(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.ResponseId, diagnostic.Event, diagnostic.MediaMessagesSent,
            diagnostic.BufferedAudioDurationMs, diagnostic.SafeReason, diagnostic.ElapsedMs,
            diagnostic.ElapsedFromFirstMediaMs, diagnostic.ElapsedFromMarkSentMs,
            diagnostic.PacketDurationMs, diagnostic.StartupBufferedAudioDurationMs,
            diagnostic.UnderflowCount, diagnostic.AveragePacingLatenessMs,
            diagnostic.MaximumPacingLatenessMs, diagnostic.SchedulerLateCount,
            diagnostic.ProducerStarvationCount, diagnostic.RemoteBufferUnderflowCount,
            diagnostic.RebufferCount, diagnostic.TotalRebufferMs,
            diagnostic.MinimumEstimatedRemoteReserveMs,
            diagnostic.MaximumEstimatedRemoteReserveMs, diagnostic.MaximumSendDurationMs,
            diagnostic.StartupTargetMs, diagnostic.LowWaterThresholdMs,
            diagnostic.HighWaterTargetMs, diagnostic.MaximumSendAheadMs,
            diagnostic.ProactiveRefillCount, diagnostic.AverageSendDurationMs);

    public void RecordLatency(VoiceLatencyDiagnostic diagnostic) =>
        LogLatency(logger, diagnostic.CallId, diagnostic.ConversationId,
            diagnostic.CorrelationId, diagnostic.TurnId, diagnostic.ResponseId,
            diagnostic.Stage, diagnostic.DurationMs, diagnostic.ElapsedFromEndpointMs,
            diagnostic.Adapter, diagnostic.Result);

    public void RecordLanguage(VoiceLanguageDiagnostic diagnostic) =>
        LogLanguage(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.StartingLanguage, diagnostic.ActiveLanguage, diagnostic.Reason,
            diagnostic.DetectionResult, diagnostic.ConfidenceBucket,
            diagnostic.AlternateEvidenceCount, diagnostic.SwitchAccepted,
            diagnostic.UnsupportedRequest, diagnostic.LanguageVersion);

    public void RecordSpeechRecognition(VoiceSpeechRecognitionDiagnostic diagnostic)
    {
        LogSpeechRecognition(logger, diagnostic.CallId, diagnostic.ConversationId,
            diagnostic.CorrelationId, diagnostic.TraceId, diagnostic.TurnId,
            diagnostic.Adapter, SafeSpeechCategory(diagnostic.ProviderOperation),
            SafeSpeechCategory(diagnostic.Stage), SafeSpeechCategory(diagnostic.ResultCategory),
            SafeSpeechCategory(diagnostic.HttpStatusCategory),
            SafeSpeechCategory(diagnostic.ContentTypeCategory),
            SafeSpeechCategory(diagnostic.ResponseShapeCategory),
            diagnostic.TranscriptPresent, diagnostic.LanguageMetadataPresent,
            SafeSpeechCategory(diagnostic.LanguageSupportCategory), diagnostic.CancellationRequested,
            diagnostic.SessionClosing, diagnostic.CallerDisconnected,
            diagnostic.RetryAttempted, SafeSpeechCategory(diagnostic.RecoveryDecision));
    }

    private static string SafeSpeechCategory(string value) => value switch
    {
        "audio_transcription" or "speech_recognition"
            or "request_started" or "response_headers_received" or "response_parsing"
            or "response_validation" or "response_completed" or "request_cancelled"
            or "adapter_completed" or "adapter_failed"
            or "success" or "empty_result" or "cancelled" or "stale_result"
            or "provider_transient" or "provider_rejected" or "invalid_response_schema"
            or "unsupported_detected_language" or "session_closing" or "caller_disconnected"
            or "timeout" or "rate_limited" or "client_error" or "server_error"
            or "json" or "event_stream" or "missing" or "unexpected"
            or "valid" or "valid_empty_transcript" or "unexpected_content_type"
            or "unexpected_root" or "unexpected_stream_event" or "provider_error_status"
            or "provider_error_envelope" or "missing_text" or "wrong_text_type"
            or "malformed_json" or "missing_final_event" or "duplicate_final_event"
            or "transcript_too_large" or "response_too_large" or "network_failure"
            or "not_received" or "not_provided" or "absent" or "supported"
            or "unsupported" or "invalid" or "mixed" or "continue" or "discard_turn"
            or "discard_stale_result" or "retry" or "end_turn" or "other" => value,
        _ => "other",
    };

    [LoggerMessage(204, LogLevel.Error,
        "Realtime voice session exception; CallId={CallId}, ConversationId={ConversationId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, ProviderCallId={ProviderCallId}, CorrelationId={CorrelationId}, Stage={Stage}, SafeCode={SafeCode}, ExceptionType={ExceptionType}, RootExceptionType={RootExceptionType}.")]
    private static partial void LogSessionException(ILogger logger, Guid callId, Guid? conversationId,
        Guid tenantId, Guid locationId, string provider, string providerCallId, Guid correlationId,
        string stage, string safeCode, string exceptionType, string rootExceptionType);

    [LoggerMessage(209, LogLevel.Information,
        "Conversation model turn; ConversationId={ConversationId}, CallSessionId={CallSessionId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, Model={Model}, HistoryTurnCount={HistoryTurnCount}, GenerationDurationMs={GenerationDurationMs}, ResultCategory={ResultCategory}, FallbackUsed={FallbackUsed}, ProviderRequestId={ProviderRequestId}, InputTokenCount={InputTokenCount}, OutputTokenCount={OutputTokenCount}.")]
    private static partial void LogModelTurn(ILogger logger, Guid conversationId, Guid callSessionId,
        Guid tenantId, Guid locationId, string provider, string model, int historyTurnCount,
        double generationDurationMs, string resultCategory, bool fallbackUsed,
        string providerRequestId, int inputTokenCount, int outputTokenCount);

    [LoggerMessage(205, LogLevel.Information,
        "Realtime inbound speech event; CallId={CallId}, CorrelationId={CorrelationId}, TurnId={TurnId}, Event={Event}, InboundFrames={InboundFrames}, DurationMs={DurationMs}, QualifiedSpeechMs={QualifiedSpeechMs}, NoiseFloor={NoiseFloor}, EnergyMetric={EnergyMetric}, SpeechQualified={SpeechQualified}, SttSubmitted={SttSubmitted}, DiscardReason={DiscardReason}.")]
    private static partial void LogInboundTurn(ILogger logger, Guid callId, Guid correlationId,
        string turnId, int inboundFrames, double durationMs, double qualifiedSpeechMs,
        double noiseFloor, double energyMetric, bool speechQualified,
        bool sttSubmitted, string discardReason, string @event);

    [LoggerMessage(206, LogLevel.Information,
        "Realtime outbound response; CallId={CallId}, CorrelationId={CorrelationId}, ResponseId={ResponseId}, SourcePcmBytes={SourcePcmBytes}, SourceSamples={SourceSamples}, ResampledSamples={ResampledSamples}, MuLawBytes={MuLawBytes}, MediaMessageCount={MediaMessageCount}, MarkSent={MarkSent}, Cleared={Cleared}, Canceled={Canceled}.")]
    private static partial void LogOutboundResponse(ILogger logger, Guid callId, Guid correlationId,
        string responseId, int sourcePcmBytes, int sourceSamples, int resampledSamples,
        int muLawBytes, int mediaMessageCount, bool markSent, bool cleared, bool canceled);

    [LoggerMessage(207, LogLevel.Information,
        "Realtime playback event; CallId={CallId}, CorrelationId={CorrelationId}, ResponseId={ResponseId}, Event={Event}, MediaMessagesSent={MediaMessagesSent}, BufferedAudioDurationMs={BufferedAudioDurationMs}, SafeReason={SafeReason}, ElapsedMs={ElapsedMs}, ElapsedFromFirstMediaMs={ElapsedFromFirstMediaMs}, ElapsedFromMarkSentMs={ElapsedFromMarkSentMs}, PacketDurationMs={PacketDurationMs}, StartupBufferedAudioDurationMs={StartupBufferedAudioDurationMs}, StartupTargetMs={StartupTargetMs}, LowWaterThresholdMs={LowWaterThresholdMs}, HighWaterTargetMs={HighWaterTargetMs}, MaximumSendAheadMs={MaximumSendAheadMs}, UnderflowCount={UnderflowCount}, AveragePacingLatenessMs={AveragePacingLatenessMs}, MaximumPacingLatenessMs={MaximumPacingLatenessMs}, SchedulerLateCount={SchedulerLateCount}, ProactiveRefillCount={ProactiveRefillCount}, ProducerStarvationCount={ProducerStarvationCount}, RemoteBufferUnderflowCount={RemoteBufferUnderflowCount}, RebufferCount={RebufferCount}, TotalRebufferMs={TotalRebufferMs}, MinimumEstimatedRemoteReserveMs={MinimumEstimatedRemoteReserveMs}, MaximumEstimatedRemoteReserveMs={MaximumEstimatedRemoteReserveMs}, AverageSendDurationMs={AverageSendDurationMs}, MaximumSendDurationMs={MaximumSendDurationMs}.")]
    private static partial void LogPlaybackEvent(ILogger logger, Guid callId, Guid correlationId,
        string responseId, string @event, int mediaMessagesSent,
        double bufferedAudioDurationMs, string safeReason, double elapsedMs,
        double elapsedFromFirstMediaMs, double elapsedFromMarkSentMs,
        double packetDurationMs, double startupBufferedAudioDurationMs,
        int underflowCount, double averagePacingLatenessMs, double maximumPacingLatenessMs,
        int schedulerLateCount, int producerStarvationCount, int remoteBufferUnderflowCount,
        int rebufferCount, double totalRebufferMs, double minimumEstimatedRemoteReserveMs,
        double maximumEstimatedRemoteReserveMs, double maximumSendDurationMs,
        double startupTargetMs, double lowWaterThresholdMs, double highWaterTargetMs,
        double maximumSendAheadMs, int proactiveRefillCount, double averageSendDurationMs);

    [LoggerMessage(210, LogLevel.Information,
        "Realtime voice latency; CallId={CallId}, ConversationId={ConversationId}, CorrelationId={CorrelationId}, TurnId={TurnId}, ResponseId={ResponseId}, Stage={Stage}, DurationMs={DurationMs}, ElapsedFromEndpointMs={ElapsedFromEndpointMs}, Adapter={Adapter}, Result={Result}.")]
    private static partial void LogLatency(ILogger logger, Guid callId, Guid? conversationId,
        Guid correlationId, string turnId, string responseId, string stage,
        double durationMs, double elapsedFromEndpointMs, string adapter, string result);

    [LoggerMessage(211, LogLevel.Information,
        "Call language decision; CallId={CallId}, CorrelationId={CorrelationId}, StartingLanguage={StartingLanguage}, ActiveLanguage={ActiveLanguage}, Reason={Reason}, DetectionResult={DetectionResult}, ConfidenceBucket={ConfidenceBucket}, AlternateEvidenceCount={AlternateEvidenceCount}, SwitchAccepted={SwitchAccepted}, UnsupportedRequest={UnsupportedRequest}, LanguageVersion={LanguageVersion}.")]
    private static partial void LogLanguage(ILogger logger, Guid callId, Guid correlationId,
        string startingLanguage, string activeLanguage, string reason, string detectionResult,
        string confidenceBucket, int alternateEvidenceCount, bool switchAccepted,
        bool unsupportedRequest, long languageVersion);

    [LoggerMessage(212, LogLevel.Information,
        "Speech recognition boundary; CallId={CallId}, ConversationId={ConversationId}, CorrelationId={CorrelationId}, TraceId={TraceId}, TurnId={TurnId}, Adapter={Adapter}, ProviderOperation={ProviderOperation}, Stage={Stage}, ResultCategory={ResultCategory}, HttpStatusCategory={HttpStatusCategory}, ContentTypeCategory={ContentTypeCategory}, ResponseShapeCategory={ResponseShapeCategory}, TranscriptPresent={TranscriptPresent}, LanguageMetadataPresent={LanguageMetadataPresent}, LanguageSupportCategory={LanguageSupportCategory}, CancellationRequested={CancellationRequested}, SessionClosing={SessionClosing}, CallerDisconnected={CallerDisconnected}, RetryAttempted={RetryAttempted}, RecoveryDecision={RecoveryDecision}.")]
    private static partial void LogSpeechRecognition(ILogger logger, Guid callId, Guid? conversationId,
        Guid correlationId, string traceId, string turnId, string adapter,
        string providerOperation, string stage, string resultCategory,
        string httpStatusCategory, string contentTypeCategory, string responseShapeCategory,
        bool transcriptPresent, bool languageMetadataPresent, string languageSupportCategory,
        bool cancellationRequested, bool sessionClosing, bool callerDisconnected,
        bool retryAttempted, string recoveryDecision);
}

public sealed class VoiceSessionConflictException(string code) : Exception("A voice session is already active for this call.")
{
    public string Code { get; } = code;
}
