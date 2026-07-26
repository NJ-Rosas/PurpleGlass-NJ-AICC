using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Application;

public interface ICallStore
{
    Task<CallSession?> GetAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken);
    Task<CallSession?> GetByProviderCallIdAsync(Guid tenantId, string providerCallId, bool tracking, CancellationToken cancellationToken);
    Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken);
    void Add(CallSession callSession);
    void AddOutboundRequest(Guid tenantId, string idempotencyKey, Guid callId, DateTimeOffset createdAtUtc);
    void AddOutbox(OutboxMessage message);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ITelephonyStore
{
    Task<CallSession?> GetByProviderIdentityAsync(string provider, string providerCallId, bool tracking, CancellationToken cancellationToken);
    Task<TelephonyNumber?> ResolveInboundNumberAsync(string provider, string normalizedNumber, CancellationToken cancellationToken);
    Task<TelephonyNumber?> ResolveOutboundNumberAsync(Guid tenantId, Guid locationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TelephonyNumber>> GetTelephonyNumbersAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<TelephonyNumber?> GetTelephonyNumberAsync(Guid tenantId, string provider, string normalizedNumber, bool tracking, CancellationToken cancellationToken);
    Task<bool> HasWebhookReceiptAsync(string provider, string eventId, CancellationToken cancellationToken);
    Task<TelephonyOperation?> GetPendingTelephonyOperationAsync(CancellationToken cancellationToken);
    Task<TelephonyOperation?> GetTelephonyOperationAsync(Guid operationId, bool tracking, CancellationToken cancellationToken);
    Task<TelephonyOperation?> GetTelephonyOperationForCallAsync(Guid tenantId, Guid callId, TelephonyOperationType type, CancellationToken cancellationToken);
    void Add(TelephonyNumber number);
    void Add(TelephonyOperation operation);
    void AddWebhookReceipt(string provider, string eventId, Guid callId, DateTimeOffset receivedAtUtc);
}
