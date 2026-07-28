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
    double ElapsedFromMarkSentMs = 0);

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
