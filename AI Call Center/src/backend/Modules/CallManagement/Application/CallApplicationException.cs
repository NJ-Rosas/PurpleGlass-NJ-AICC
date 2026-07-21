namespace PurpleGlass.Modules.CallManagement.Application;

public sealed class CallApplicationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public static CallApplicationException NotFound() => new("call_not_found", "The call was not found.");
    public static CallApplicationException Concurrency(Exception? inner = null) => new("concurrency_conflict", "The call changed concurrently.", inner);
    public static CallApplicationException InvalidState(Exception inner) => new("invalid_call_state", "The call state transition is invalid.", inner);
    public static CallApplicationException IdempotencyConflict() => new("idempotency_conflict", "The idempotency key was used for a different request.");
}

public sealed class CallPersistenceConcurrencyException(Exception innerException)
    : Exception("The call changed concurrently.", innerException);
