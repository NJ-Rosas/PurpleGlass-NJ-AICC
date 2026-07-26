using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Infrastructure;

public sealed class CallManagementDbContext(DbContextOptions<CallManagementDbContext> options)
    : DbContext(options), ICallStore, ITelephonyStore
{
    public DbSet<CallSession> Calls => Set<CallSession>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    internal DbSet<OutboundRequestReceipt> OutboundRequests => Set<OutboundRequestReceipt>();
    public DbSet<TelephonyNumber> TelephonyNumbers => Set<TelephonyNumber>();
    public DbSet<TelephonyOperation> TelephonyOperations => Set<TelephonyOperation>();
    internal DbSet<TelephonyWebhookReceipt> TelephonyWebhookReceipts => Set<TelephonyWebhookReceipt>();

    public async Task<CallSession?> GetAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<CallSession> query = tracking ? Calls : Calls.AsNoTracking();
        return await query.SingleOrDefaultAsync(call => call.TenantId == new TenantId(tenantId) && call.Id == new CallSessionId(callId), cancellationToken);
    }

    public async Task<CallSession?> GetByProviderCallIdAsync(Guid tenantId, string providerCallId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<CallSession> query = tracking ? Calls : Calls.AsNoTracking();
        return await query.SingleOrDefaultAsync(call => call.TenantId == new TenantId(tenantId) && call.ProviderCallId == providerCallId, cancellationToken);
    }

    public async Task<CallSession?> GetByProviderIdentityAsync(string provider, string providerCallId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<CallSession> query = tracking ? Calls : Calls.AsNoTracking();
        return await query.SingleOrDefaultAsync(call => call.Provider == provider && call.ProviderCallId == providerCallId, cancellationToken);
    }

    public async Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        await (from receipt in OutboundRequests.AsNoTracking()
               join call in Calls.AsNoTracking() on receipt.CallId equals call.Id
               where receipt.TenantId == tenantId && receipt.IdempotencyKey == idempotencyKey
               select call).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken) =>
        await Calls.AsNoTracking().Where(call => call.TenantId == new TenantId(tenantId)
            && (!locationId.HasValue || call.LocationId == new LocationId(locationId.Value)))
            .OrderByDescending(call => call.CreatedAtUtc).Take(limit).ToListAsync(cancellationToken);

    public Task<TelephonyNumber?> ResolveInboundNumberAsync(string provider, string normalizedNumber, CancellationToken cancellationToken) =>
        TelephonyNumbers.AsNoTracking().SingleOrDefaultAsync(number => number.Provider == provider
            && number.NormalizedNumber == normalizedNumber && number.IsActive && number.InboundEnabled, cancellationToken);

    public Task<TelephonyNumber?> ResolveOutboundNumberAsync(Guid tenantId, Guid locationId, CancellationToken cancellationToken) =>
        TelephonyNumbers.AsNoTracking().SingleOrDefaultAsync(number => number.TenantId == new TenantId(tenantId)
            && number.LocationId == new LocationId(locationId) && number.IsActive && number.OutboundEnabled, cancellationToken);

    public async Task<IReadOnlyList<TelephonyNumber>> GetTelephonyNumbersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await TelephonyNumbers.AsNoTracking().Where(number => number.TenantId == new TenantId(tenantId))
            .OrderBy(number => number.NormalizedNumber).ToListAsync(cancellationToken);

    public async Task<TelephonyNumber?> GetTelephonyNumberAsync(Guid tenantId, string provider, string normalizedNumber, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<TelephonyNumber> query = tracking ? TelephonyNumbers : TelephonyNumbers.AsNoTracking();
        return await query.SingleOrDefaultAsync(number => number.TenantId == new TenantId(tenantId)
            && number.Provider == provider && number.NormalizedNumber == normalizedNumber, cancellationToken);
    }

    public Task<bool> HasWebhookReceiptAsync(string provider, string eventId, CancellationToken cancellationToken) =>
        TelephonyWebhookReceipts.AsNoTracking().AnyAsync(receipt => receipt.Provider == provider && receipt.EventId == eventId, cancellationToken);

    public Task<TelephonyOperation?> GetPendingTelephonyOperationAsync(CancellationToken cancellationToken) =>
        TelephonyOperations.OrderBy(operation => operation.CreatedAtUtc)
            .FirstOrDefaultAsync(operation => operation.State == TelephonyOperationState.Pending, cancellationToken);

    public async Task<TelephonyOperation?> GetTelephonyOperationAsync(Guid operationId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<TelephonyOperation> query = tracking ? TelephonyOperations : TelephonyOperations.AsNoTracking();
        return await query.SingleOrDefaultAsync(operation => operation.Id == operationId, cancellationToken);
    }

    public Task<TelephonyOperation?> GetTelephonyOperationForCallAsync(Guid tenantId, Guid callId, TelephonyOperationType type, CancellationToken cancellationToken) =>
        TelephonyOperations.AsNoTracking().SingleOrDefaultAsync(operation => operation.TenantId == new TenantId(tenantId)
            && operation.CallId == new CallSessionId(callId) && operation.Type == type, cancellationToken);

    public void Add(CallSession callSession) => Calls.Add(callSession);
    public void Add(TelephonyNumber number) => TelephonyNumbers.Add(number);
    public void Add(TelephonyOperation operation) => TelephonyOperations.Add(operation);
    public void AddWebhookReceipt(string provider, string eventId, Guid callId, DateTimeOffset receivedAtUtc) =>
        TelephonyWebhookReceipts.Add(new TelephonyWebhookReceipt(Guid.NewGuid(), provider, eventId, new CallSessionId(callId), receivedAtUtc));
    public void AddOutboundRequest(Guid tenantId, string idempotencyKey, Guid callId, DateTimeOffset createdAtUtc) =>
        OutboundRequests.Add(new OutboundRequestReceipt(Guid.NewGuid(), tenantId, idempotencyKey, new CallSessionId(callId), createdAtUtc));
    public void AddOutbox(OutboxMessage message) => OutboxMessages.Add(message);

    async Task ICallStore.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try { _ = await SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new CallPersistenceConcurrencyException(exception); }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCall(modelBuilder.Entity<CallSession>());
        ConfigureReceipt(modelBuilder.Entity<OutboundRequestReceipt>());
        ConfigureTelephonyNumber(modelBuilder.Entity<TelephonyNumber>());
        ConfigureTelephonyOperation(modelBuilder.Entity<TelephonyOperation>());
        ConfigureWebhookReceipt(modelBuilder.Entity<TelephonyWebhookReceipt>());
        ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }

    private static void ConfigureCall(EntityTypeBuilder<CallSession> call)
    {
        call.ToTable("call_sessions", "call_management");
        call.HasKey(entity => entity.Id);
        call.Property(entity => entity.Id).HasConversion(id => id.Value, value => new CallSessionId(value));
        call.Property(entity => entity.TenantId).HasConversion(id => id.Value, value => new TenantId(value));
        call.Property(entity => entity.LocationId).HasConversion(id => id.Value, value => new LocationId(value));
        call.Property(entity => entity.ProviderCallId).HasMaxLength(200);
        call.Property(entity => entity.Provider).HasMaxLength(50).HasDefaultValue("Synthetic").IsRequired();
        call.Property(entity => entity.ProviderParentCallId).HasMaxLength(200);
        call.Property(entity => entity.FromNumber).HasMaxLength(32).IsRequired();
        call.Property(entity => entity.ToNumber).HasMaxLength(32).IsRequired();
        call.Property(entity => entity.Outcome).HasMaxLength(100);
        call.Property(entity => entity.RecordingReference).HasMaxLength(500);
        call.Property(entity => entity.RecordingStorageProvider).HasMaxLength(100);
        call.Property(entity => entity.RecordingContentType).HasMaxLength(150);
        call.Property(entity => entity.RecordingChecksum).HasMaxLength(200);
        call.Property(entity => entity.Version).IsConcurrencyToken();
        call.HasIndex(entity => new { entity.TenantId, entity.Id }).IsUnique();
        call.HasIndex(entity => new { entity.TenantId, entity.LocationId, entity.CreatedAtUtc });
        call.HasIndex(entity => new { entity.Provider, entity.ProviderCallId }).IsUnique().HasFilter("\"ProviderCallId\" IS NOT NULL");
    }

    private static void ConfigureTelephonyNumber(EntityTypeBuilder<TelephonyNumber> number)
    {
        number.ToTable("telephony_numbers", "call_management");
        number.HasKey(entity => entity.Id);
        number.Property(entity => entity.TenantId).HasConversion(id => id.Value, value => new TenantId(value));
        number.Property(entity => entity.LocationId).HasConversion(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            value => value.HasValue ? new LocationId(value.Value) : null);
        number.Property(entity => entity.Provider).HasMaxLength(50).IsRequired();
        number.Property(entity => entity.NormalizedNumber).HasMaxLength(16).IsRequired();
        number.Property(entity => entity.ProviderNumberId).HasMaxLength(200);
        number.Property(entity => entity.Version).IsConcurrencyToken();
        number.HasIndex(entity => new { entity.Provider, entity.NormalizedNumber }).IsUnique();
        number.HasIndex(entity => new { entity.TenantId, entity.LocationId }).IsUnique()
            .HasFilter("\"OutboundEnabled\" AND \"IsActive\" AND \"LocationId\" IS NOT NULL");
    }

    private static void ConfigureTelephonyOperation(EntityTypeBuilder<TelephonyOperation> operation)
    {
        operation.ToTable("telephony_operations", "call_management");
        operation.HasKey(entity => entity.Id);
        operation.Property(entity => entity.TenantId).HasConversion(id => id.Value, value => new TenantId(value));
        operation.Property(entity => entity.LocationId).HasConversion(id => id.Value, value => new LocationId(value));
        operation.Property(entity => entity.CallId).HasConversion(id => id.Value, value => new CallSessionId(value));
        operation.Property(entity => entity.SafeErrorCode).HasMaxLength(100);
        operation.Property(entity => entity.State).IsConcurrencyToken();
        operation.HasIndex(entity => new { entity.State, entity.CreatedAtUtc });
        operation.HasIndex(entity => new { entity.TenantId, entity.CallId, entity.Type }).IsUnique();
    }

    private static void ConfigureWebhookReceipt(EntityTypeBuilder<TelephonyWebhookReceipt> receipt)
    {
        receipt.ToTable("telephony_webhook_receipts", "call_management");
        receipt.HasKey(entity => entity.Id);
        receipt.Property(entity => entity.Provider).HasMaxLength(50).IsRequired();
        receipt.Property(entity => entity.EventId).HasMaxLength(200).IsRequired();
        receipt.Property(entity => entity.CallId).HasConversion(id => id.Value, value => new CallSessionId(value));
        receipt.HasIndex(entity => new { entity.Provider, entity.EventId }).IsUnique();
    }

    private static void ConfigureReceipt(EntityTypeBuilder<OutboundRequestReceipt> receipt)
    {
        receipt.ToTable("outbound_request_receipts", "call_management");
        receipt.HasKey(entity => entity.Id);
        receipt.Property(entity => entity.IdempotencyKey).HasMaxLength(200).IsRequired();
        receipt.Property(entity => entity.CallId).HasConversion(id => id.Value, value => new CallSessionId(value));
        receipt.HasIndex(entity => new { entity.TenantId, entity.IdempotencyKey }).IsUnique();
    }

    private static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> outbox)
    {
        outbox.ToTable("outbox_messages", "eventing", table => table.ExcludeFromMigrations());
        outbox.Ignore(entity => entity.LeaseId);
        outbox.Ignore(entity => entity.LeaseExpiresAtUtc);
        outbox.HasKey(entity => entity.Id);
        outbox.Property(entity => entity.Topic).HasMaxLength(500).IsRequired();
        outbox.Property(entity => entity.MessageType).HasMaxLength(200).IsRequired();
        outbox.Property(entity => entity.Payload).HasColumnType("jsonb").IsRequired();
        outbox.Property(entity => entity.Producer).HasMaxLength(100).IsRequired();
        outbox.Property(entity => entity.DataClassification).HasMaxLength(50).IsRequired();
        outbox.Property(entity => entity.Status).HasMaxLength(30).IsRequired();
        outbox.Property(entity => entity.TraceId).HasMaxLength(100);
        outbox.Property(entity => entity.TraceParent).HasMaxLength(100);
        outbox.Property(entity => entity.TraceState).HasMaxLength(512);
        outbox.Property(entity => entity.LastError).HasMaxLength(1_000);
    }
}

internal sealed class OutboundRequestReceipt
{
    private OutboundRequestReceipt() { }
    public OutboundRequestReceipt(Guid id, Guid tenantId, string idempotencyKey, CallSessionId callId, DateTimeOffset createdAtUtc)
    { Id = id; TenantId = tenantId; IdempotencyKey = idempotencyKey; CallId = callId; CreatedAtUtc = createdAtUtc; }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public CallSessionId CallId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}

internal sealed class TelephonyWebhookReceipt
{
    private TelephonyWebhookReceipt() { }
    public TelephonyWebhookReceipt(Guid id, string provider, string eventId, CallSessionId callId, DateTimeOffset receivedAtUtc)
    { Id = id; Provider = provider; EventId = eventId; CallId = callId; ReceivedAtUtc = receivedAtUtc; }
    public Guid Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public CallSessionId CallId { get; private set; }
    public DateTimeOffset ReceivedAtUtc { get; private set; }
}
