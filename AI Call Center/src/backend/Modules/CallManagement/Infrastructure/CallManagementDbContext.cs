using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.Modules.CallManagement.Infrastructure;

public sealed class CallManagementDbContext(DbContextOptions<CallManagementDbContext> options)
    : DbContext(options), ICallStore
{
    public DbSet<CallSession> Calls => Set<CallSession>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    internal DbSet<OutboundRequestReceipt> OutboundRequests => Set<OutboundRequestReceipt>();

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

    public async Task<CallSession?> GetByOutboundKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        await (from receipt in OutboundRequests.AsNoTracking()
               join call in Calls.AsNoTracking() on receipt.CallId equals call.Id
               where receipt.TenantId == tenantId && receipt.IdempotencyKey == idempotencyKey
               select call).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CallSession>> GetRecentAsync(Guid tenantId, Guid? locationId, int limit, CancellationToken cancellationToken) =>
        await Calls.AsNoTracking().Where(call => call.TenantId == new TenantId(tenantId)
            && (!locationId.HasValue || call.LocationId == new LocationId(locationId.Value)))
            .OrderByDescending(call => call.CreatedAtUtc).Take(limit).ToListAsync(cancellationToken);

    public void Add(CallSession callSession) => Calls.Add(callSession);
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
        call.HasIndex(entity => new { entity.TenantId, entity.ProviderCallId }).IsUnique().HasFilter("\"ProviderCallId\" IS NOT NULL");
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
        outbox.HasKey(entity => entity.Id);
        outbox.Property(entity => entity.Topic).HasMaxLength(500).IsRequired();
        outbox.Property(entity => entity.MessageType).HasMaxLength(200).IsRequired();
        outbox.Property(entity => entity.Payload).HasColumnType("jsonb").IsRequired();
        outbox.Property(entity => entity.Producer).HasMaxLength(100).IsRequired();
        outbox.Property(entity => entity.DataClassification).HasMaxLength(50).IsRequired();
        outbox.Property(entity => entity.Status).HasMaxLength(30).IsRequired();
        outbox.Property(entity => entity.TraceId).HasMaxLength(100);
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
