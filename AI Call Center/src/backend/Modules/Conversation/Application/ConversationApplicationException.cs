namespace PurpleGlass.Modules.Conversation.Application;

public sealed class ConversationApplicationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public static ConversationApplicationException NotFound() => new("conversation_not_found", "The conversation was not found.");
    public static ConversationApplicationException CallNotFound() => new("call_not_found", "The associated call was not found.");
    public static ConversationApplicationException TenantMismatch() => new("tenant_mismatch", "The call does not belong to the requested tenant or location.");
    public static ConversationApplicationException CallNotEligible() => new("call_not_eligible", "The call is not eligible for a conversation.");
    public static ConversationApplicationException AlreadyExists() => new("conversation_already_exists", "A conversation already exists for this call.");
    public static ConversationApplicationException Concurrency(Exception? inner = null) => new("concurrency_conflict", "The conversation changed concurrently.", inner);
    public static ConversationApplicationException InvalidState(Exception inner) => new("invalid_conversation_state", "The conversation state transition is invalid.", inner);
    public static ConversationApplicationException IdempotencyConflict() => new("idempotency_conflict", "The command identifier was used for different content.");
}

public sealed class ConversationPersistenceConcurrencyException(Exception innerException)
    : Exception("The conversation changed concurrently.", innerException);
