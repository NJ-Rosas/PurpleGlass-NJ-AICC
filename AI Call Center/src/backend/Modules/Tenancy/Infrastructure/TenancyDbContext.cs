using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Audit.Domain;
using PurpleGlass.Modules.Audit.Infrastructure;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.Modules.Tenancy.Infrastructure;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options)
    : DbContext(options), ITenancyStore, IAuditWriter
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public async Task<TenantSummary?> GetSummaryAsync(
        TenantId tenantId,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        return await (
            from tenant in Tenants.AsNoTracking()
            join location in Locations.AsNoTracking() on tenant.Id equals location.TenantId
            where tenant.Id == tenantId
                && location.Id == locationId
                && tenant.IsActive
                && location.IsActive
            select new TenantSummary(
                tenant.Id.Value,
                tenant.DisplayName,
                location.Id.Value,
                location.DisplayName,
                location.TimeZoneId,
                location.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<Location?> GetLocationAsync(
        TenantId tenantId,
        LocationId locationId,
        CancellationToken cancellationToken) =>
        Locations.SingleOrDefaultAsync(
            location => location.TenantId == tenantId && location.Id == locationId,
            cancellationToken);

    public void AddOutbox(OutboxMessage message) => OutboxMessages.Add(message);

    public void Add(AuditRecord record) => AuditRecords.Add(record);

    async Task ITenancyStore.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new TenancyConcurrencyException(exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenant(modelBuilder.Entity<Tenant>());
        ConfigureLocation(modelBuilder.Entity<Location>());
        ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
        AuditModelConfiguration.Configure(modelBuilder);
    }

    private static void ConfigureTenant(EntityTypeBuilder<Tenant> tenant)
    {
        tenant.ToTable("tenants", "tenancy");
        tenant.HasKey(entity => entity.Id);
        tenant.Property(entity => entity.Id)
            .HasConversion(id => id.Value, value => new TenantId(value));
        tenant.Property(entity => entity.DisplayName).HasMaxLength(200).IsRequired();
    }

    private static void ConfigureLocation(EntityTypeBuilder<Location> location)
    {
        location.ToTable("locations", "tenancy");
        location.HasKey(entity => entity.Id);
        location.Property(entity => entity.Id)
            .HasConversion(id => id.Value, value => new LocationId(value));
        location.Property(entity => entity.TenantId)
            .HasConversion(id => id.Value, value => new TenantId(value));
        location.Property(entity => entity.DisplayName).HasMaxLength(160).IsRequired();
        location.Property(entity => entity.TimeZoneId).HasMaxLength(100).IsRequired();
        location.Property(entity => entity.Version).IsConcurrencyToken();
        location.HasIndex(entity => new { entity.TenantId, entity.Id }).IsUnique();
    }

    private static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> outbox)
    {
        outbox.ToTable("outbox_messages", "eventing");
        outbox.HasKey(message => message.Id);
        outbox.Property(message => message.Topic).HasMaxLength(500).IsRequired();
        outbox.Property(message => message.MessageType).HasMaxLength(200).IsRequired();
        outbox.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        outbox.Property(message => message.LastError).HasMaxLength(1_000);
        outbox.HasIndex(message => new { message.PublishedAtUtc, message.OccurredAtUtc });
    }
}
