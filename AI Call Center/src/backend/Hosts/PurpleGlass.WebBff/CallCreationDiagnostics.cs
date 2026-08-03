using System.Diagnostics;
using PurpleGlass.Modules.CallManagement.Application;

namespace PurpleGlass.WebBff;

public sealed class CallCreationDiagnosticState : ICallCreationDiagnostics
{
    public string Stage { get; private set; } = "model_binding";
    public Guid? TenantId { get; private set; }
    public Guid? LocationId { get; private set; }
    public Guid? CallId { get; private set; }
    public bool TransactionBegan { get; private set; }
    public bool CallPersisted { get; private set; }
    public bool OutboxPersisted { get; private set; }
    public bool DispatchPersisted { get; private set; }
    public bool ProviderDispatchOccurred => false;

    public void SetContext(Guid tenantId, Guid locationId)
    {
        TenantId = tenantId;
        LocationId = locationId;
        AddActivityEvent("context_set");
    }

    public void Mark(string stage, Guid? callId = null)
    {
        Stage = stage;
        if (callId.HasValue) CallId = callId;
        if (stage == "persistence_started") TransactionBegan = true;
        AddActivityEvent(stage);
    }

    public void MarkPersistenceCompleted(Guid callId)
    {
        CallId = callId;
        Stage = "transaction_committed";
        CallPersisted = true;
        OutboxPersisted = true;
        DispatchPersisted = true;
        AddActivityEvent(Stage);
    }

    private static void AddActivityEvent(string stage)
    {
        Activity? activity = Activity.Current;
        activity?.SetTag("purpleglass.call_creation.stage", stage);
        activity?.AddEvent(new ActivityEvent($"call_creation.{stage}"));
    }
}

public static partial class CallCreationLog
{
    [LoggerMessage(EventId = 220, Level = LogLevel.Information,
        Message = "Outbound call creation completed; TraceId={TraceId}, CorrelationId={CorrelationId}, Route={Route}, Method={Method}, Role={Role}, TenantId={TenantId}, LocationId={LocationId}, CallId={CallId}, Stage={Stage}, TransactionBegan={TransactionBegan}, CallPersisted={CallPersisted}, OutboxPersisted={OutboxPersisted}, DispatchPersisted={DispatchPersisted}, ProviderDispatchOccurred={ProviderDispatchOccurred}.")]
    public static partial void Completed(ILogger logger, string traceId, Guid correlationId,
        string route, string method, string role, Guid? tenantId, Guid? locationId, Guid? callId,
        string stage, bool transactionBegan, bool callPersisted, bool outboxPersisted,
        bool dispatchPersisted, bool providerDispatchOccurred);

    [LoggerMessage(EventId = 221, Level = LogLevel.Error,
        Message = "BFF request failed safely; TraceId={TraceId}, CorrelationId={CorrelationId}, Route={Route}, Method={Method}, Role={Role}, TenantId={TenantId}, LocationId={LocationId}, CallId={CallId}, SafeCategory={SafeCategory}, Stage={Stage}, TransactionBegan={TransactionBegan}, CallPersisted={CallPersisted}, OutboxPersisted={OutboxPersisted}, DispatchPersisted={DispatchPersisted}, ProviderDispatchOccurred={ProviderDispatchOccurred}, ExceptionType={ExceptionType}.")]
    public static partial void Failed(ILogger logger, string traceId, Guid correlationId,
        string route, string method, string role, Guid? tenantId, Guid? locationId, Guid? callId,
        string safeCategory, string stage, bool transactionBegan, bool callPersisted,
        bool outboxPersisted, bool dispatchPersisted, bool providerDispatchOccurred,
        string exceptionType);

    [LoggerMessage(EventId = 222, Level = LogLevel.Warning,
        Message = "BFF request rejected safely; TraceId={TraceId}, CorrelationId={CorrelationId}, Route={Route}, Method={Method}, Role={Role}, TenantId={TenantId}, LocationId={LocationId}, CallId={CallId}, SafeCategory={SafeCategory}, Stage={Stage}, TransactionBegan={TransactionBegan}, CallPersisted={CallPersisted}, OutboxPersisted={OutboxPersisted}, DispatchPersisted={DispatchPersisted}, ProviderDispatchOccurred={ProviderDispatchOccurred}, ExceptionType={ExceptionType}.")]
    public static partial void Rejected(ILogger logger, string traceId, Guid correlationId,
        string route, string method, string role, Guid? tenantId, Guid? locationId, Guid? callId,
        string safeCategory, string stage, bool transactionBegan, bool callPersisted,
        bool outboxPersisted, bool dispatchPersisted, bool providerDispatchOccurred,
        string exceptionType);
}
