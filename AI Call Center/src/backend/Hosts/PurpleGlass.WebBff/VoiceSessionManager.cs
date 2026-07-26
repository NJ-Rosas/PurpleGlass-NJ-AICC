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

        LogSessionStarted(logger, call.CallId, call.Provider, call.Direction);
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
        "Voice session started; CallId={CallId}, Provider={Provider}, Direction={Direction}.")]
    private static partial void LogSessionStarted(ILogger logger, Guid callId, string provider, string direction);

    [LoggerMessage(201, LogLevel.Information,
        "Voice session ended; CallId={CallId}, Provider={Provider}, State={State}.")]
    private static partial void LogSessionEnded(ILogger logger, Guid callId, string provider, string state);

    [LoggerMessage(202, LogLevel.Information,
        "Voice session stop requested; CallId={CallId}, Reason={Reason}.")]
    private static partial void LogSessionStopRequested(ILogger logger, Guid callId, string reason);
}

public sealed class BffVoiceSessionStateSink(RealtimeEventHub realtime) : IVoiceSessionStateSink
{
    public ValueTask PublishAsync(VoiceSessionStateChange change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (change.State == VoiceSessionState.Ended) return ValueTask.CompletedTask;
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
}

public sealed class VoiceSessionConflictException(string code) : Exception("A voice session is already active for this call.")
{
    public string Code { get; } = code;
}
