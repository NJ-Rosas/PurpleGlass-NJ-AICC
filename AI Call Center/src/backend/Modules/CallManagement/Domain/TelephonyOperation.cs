namespace PurpleGlass.Modules.CallManagement.Domain;

public enum TelephonyOperationType
{
    StartOutbound = 1,
    Hangup = 2,
}

public enum TelephonyOperationState
{
    Pending = 1,
    Dispatching = 2,
    Completed = 3,
    Failed = 4,
}

public sealed class TelephonyOperation
{
    private TelephonyOperation() { }

    public TelephonyOperation(Guid id, TenantId tenantId, LocationId locationId, CallSessionId callId,
        TelephonyOperationType type, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Identifier is required.", nameof(id));
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        CallId = callId;
        Type = type;
        State = TelephonyOperationState.Pending;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public LocationId LocationId { get; private set; }
    public CallSessionId CallId { get; private set; }
    public TelephonyOperationType Type { get; private set; }
    public TelephonyOperationState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DispatchStartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? SafeErrorCode { get; private set; }

    public void BeginDispatch(DateTimeOffset now)
    {
        if (State != TelephonyOperationState.Pending) throw new InvalidOperationException("Operation is not pending.");
        State = TelephonyOperationState.Dispatching;
        DispatchStartedAtUtc = now;
    }

    public void Complete(DateTimeOffset now)
    {
        if (State != TelephonyOperationState.Dispatching) throw new InvalidOperationException("Operation is not dispatching.");
        State = TelephonyOperationState.Completed;
        CompletedAtUtc = now;
    }

    public void Fail(string safeErrorCode, DateTimeOffset now)
    {
        if (State != TelephonyOperationState.Dispatching) throw new InvalidOperationException("Operation is not dispatching.");
        string normalized = safeErrorCode.Trim();
        if (normalized.Length is 0 or > 100) throw new ArgumentException("A bounded safe error code is required.", nameof(safeErrorCode));
        State = TelephonyOperationState.Failed;
        SafeErrorCode = normalized;
        CompletedAtUtc = now;
    }
}
