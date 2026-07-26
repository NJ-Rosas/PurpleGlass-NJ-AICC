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
                call.CorrelationId), transport, cancellationToken);
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

    public void RecordInboundTurn(VoiceInboundTurnDiagnostic diagnostic) =>
        LogInboundTurn(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.TurnId, diagnostic.InboundFrames, diagnostic.DurationMs,
            diagnostic.SpeechQualified, diagnostic.SttSubmitted,
            diagnostic.DiscardReason ?? "none");

    public void RecordOutboundResponse(VoiceOutboundResponseDiagnostic diagnostic) =>
        LogOutboundResponse(logger, diagnostic.CallId, diagnostic.CorrelationId,
            diagnostic.ResponseId, diagnostic.SourcePcmBytes, diagnostic.SourceSamples,
            diagnostic.ResampledSamples, diagnostic.MuLawBytes, diagnostic.MediaMessageCount,
            diagnostic.MarkSent, diagnostic.Cleared, diagnostic.Canceled);

    [LoggerMessage(204, LogLevel.Error,
        "Realtime voice session exception; CallId={CallId}, ConversationId={ConversationId}, TenantId={TenantId}, LocationId={LocationId}, Provider={Provider}, ProviderCallId={ProviderCallId}, CorrelationId={CorrelationId}, Stage={Stage}, SafeCode={SafeCode}, ExceptionType={ExceptionType}, RootExceptionType={RootExceptionType}.")]
    private static partial void LogSessionException(ILogger logger, Guid callId, Guid? conversationId,
        Guid tenantId, Guid locationId, string provider, string providerCallId, Guid correlationId,
        string stage, string safeCode, string exceptionType, string rootExceptionType);

    [LoggerMessage(205, LogLevel.Information,
        "Realtime inbound turn; CallId={CallId}, CorrelationId={CorrelationId}, TurnId={TurnId}, InboundFrames={InboundFrames}, DurationMs={DurationMs}, SpeechQualified={SpeechQualified}, SttSubmitted={SttSubmitted}, DiscardReason={DiscardReason}.")]
    private static partial void LogInboundTurn(ILogger logger, Guid callId, Guid correlationId,
        string turnId, int inboundFrames, double durationMs, bool speechQualified,
        bool sttSubmitted, string discardReason);

    [LoggerMessage(206, LogLevel.Information,
        "Realtime outbound response; CallId={CallId}, CorrelationId={CorrelationId}, ResponseId={ResponseId}, SourcePcmBytes={SourcePcmBytes}, SourceSamples={SourceSamples}, ResampledSamples={ResampledSamples}, MuLawBytes={MuLawBytes}, MediaMessageCount={MediaMessageCount}, MarkSent={MarkSent}, Cleared={Cleared}, Canceled={Canceled}.")]
    private static partial void LogOutboundResponse(ILogger logger, Guid callId, Guid correlationId,
        string responseId, int sourcePcmBytes, int sourceSamples, int resampledSamples,
        int muLawBytes, int mediaMessageCount, bool markSent, bool cleared, bool canceled);
}

public sealed class VoiceSessionConflictException(string code) : Exception("A voice session is already active for this call.")
{
    public string Code { get; } = code;
}
