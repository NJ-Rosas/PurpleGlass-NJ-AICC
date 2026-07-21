using PurpleGlass.Modules.Conversation.Domain;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed record CreateConversation(Guid TenantId, Guid LocationId, Guid CallId, Guid CorrelationId, string ConfigurationVersion, string Language);
public sealed record ChangeConversationState(Guid TenantId, Guid ConversationId, long ExpectedVersion);
public sealed record AddConversationTurn(Guid TenantId, Guid ConversationId, long ExpectedVersion, Guid TurnId, string Text, decimal? RecognitionConfidence = null, bool SafetyFlagged = false, bool EscalationFlagged = false);
public sealed record RecordConversationEscalation(Guid TenantId, Guid ConversationId, long ExpectedVersion, string Reason);
public sealed record CompleteConversation(Guid TenantId, Guid ConversationId, long ExpectedVersion, ConversationSummary Summary);
