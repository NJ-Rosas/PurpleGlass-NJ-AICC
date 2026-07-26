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
}

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
