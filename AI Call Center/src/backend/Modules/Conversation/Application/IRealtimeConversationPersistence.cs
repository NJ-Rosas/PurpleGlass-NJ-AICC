using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;

namespace PurpleGlass.Modules.Conversation.Application;

public interface IRealtimeConversationPersistence
{
    Task<ConversationStatusProjection> CreateAsync(CreateConversation command, CancellationToken cancellationToken);
    Task<ConversationStatusProjection> ActivateAsync(ChangeConversationState command, CancellationToken cancellationToken);
    Task<ConversationStatusProjection> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LiveTranscriptTurn>> GetTranscriptAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
    Task<LiveTranscriptTurn> AddCallerTurnAsync(AddConversationTurn command, CancellationToken cancellationToken);
    Task<LiveTranscriptTurn> AddAssistantTurnAsync(AddConversationTurn command, CancellationToken cancellationToken);
    Task<CompletedConversationSummary> CompleteAsync(CompleteConversation command, CancellationToken cancellationToken);
    Task<ConversationStatusProjection> FailAsync(ChangeConversationState command, CancellationToken cancellationToken);
}

public sealed class VoicePersistenceException(
    string code,
    string stage,
    Exception innerException)
    : Exception("Realtime voice persistence failed.", innerException)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage;
}

public interface IVoiceSessionDiagnostics
{
    void RecordException(VoiceSessionExceptionDiagnostic diagnostic);
    void RecordModelTurn(ConversationModelTurnDiagnostic diagnostic) { }
    void RecordInboundTurn(VoiceInboundTurnDiagnostic diagnostic) { }
    void RecordOutboundResponse(VoiceOutboundResponseDiagnostic diagnostic) { }
    void RecordPlaybackEvent(VoicePlaybackEventDiagnostic diagnostic) { }
    void RecordLatency(VoiceLatencyDiagnostic diagnostic) { }
}

public sealed record ConversationModelTurnDiagnostic(
    Guid ConversationId,
    Guid CallSessionId,
    Guid TenantId,
    Guid LocationId,
    string Provider,
    string Model,
    int HistoryTurnCount,
    double GenerationDurationMs,
    string ResultCategory,
    bool FallbackUsed,
    string? ProviderRequestId,
    int InputTokenCount,
    int OutputTokenCount);

public sealed record VoiceInboundTurnDiagnostic(
    Guid CallId,
    Guid CorrelationId,
    string TurnId,
    int InboundFrames,
    double DurationMs,
    double QualifiedSpeechMs,
    double NoiseFloor,
    double EnergyMetric,
    bool SpeechQualified,
    bool SttSubmitted,
    string? DiscardReason,
    string Event);

public sealed record VoiceOutboundResponseDiagnostic(
    Guid CallId,
    Guid CorrelationId,
    string ResponseId,
    int SourcePcmBytes,
    int SourceSamples,
    int ResampledSamples,
    int MuLawBytes,
    int MediaMessageCount,
    bool MarkSent,
    bool Cleared,
    bool Canceled);

public sealed record VoicePlaybackEventDiagnostic(
    Guid CallId,
    Guid CorrelationId,
    string ResponseId,
    string Event,
    int MediaMessagesSent,
    double BufferedAudioDurationMs,
    string SafeReason,
    double ElapsedMs = 0,
    double ElapsedFromFirstMediaMs = 0,
    double ElapsedFromMarkSentMs = 0,
    double PacketDurationMs = 0,
    double StartupBufferedAudioDurationMs = 0,
    int UnderflowCount = 0,
    double AveragePacingLatenessMs = 0,
    double MaximumPacingLatenessMs = 0,
    int SchedulerLateCount = 0,
    int ProducerStarvationCount = 0,
    int RemoteBufferUnderflowCount = 0,
    int RebufferCount = 0,
    double TotalRebufferMs = 0,
    double MinimumEstimatedRemoteReserveMs = 0,
    double MaximumEstimatedRemoteReserveMs = 0,
    double MaximumSendDurationMs = 0,
    double StartupTargetMs = 0,
    double LowWaterThresholdMs = 0,
    double HighWaterTargetMs = 0,
    double MaximumSendAheadMs = 0,
    int ProactiveRefillCount = 0,
    double AverageSendDurationMs = 0);

public sealed record VoiceLatencyDiagnostic(
    Guid CallId,
    Guid? ConversationId,
    Guid CorrelationId,
    string TurnId,
    string ResponseId,
    string Stage,
    double DurationMs,
    double ElapsedFromEndpointMs,
    string Adapter,
    string Result);

public sealed record VoiceSessionExceptionDiagnostic(
    Guid CallId,
    Guid? ConversationId,
    Guid TenantId,
    Guid LocationId,
    string Provider,
    string ProviderCallId,
    Guid CorrelationId,
    string Stage,
    string SafeCode,
    string ExceptionType,
    string RootExceptionType);
