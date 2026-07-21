using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed class EventingDbContext(DbContextOptions<EventingDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder.Entity<OutboxMessage>());

    public static void Configure(EntityTypeBuilder<OutboxMessage> outbox)
    {
        outbox.ToTable("outbox_messages", "eventing");
        outbox.HasKey(entity => entity.Id);
        outbox.Property(entity => entity.Topic).HasMaxLength(500).IsRequired();
        outbox.Property(entity => entity.MessageType).HasMaxLength(200).IsRequired();
        outbox.Property(entity => entity.Payload).HasColumnType("jsonb").IsRequired();
        outbox.Property(entity => entity.Producer).HasMaxLength(100).IsRequired();
        outbox.Property(entity => entity.DataClassification).HasMaxLength(50).IsRequired();
        outbox.Property(entity => entity.Status).HasMaxLength(30).IsRequired();
        outbox.Property(entity => entity.TraceId).HasMaxLength(100);
        outbox.Property(entity => entity.LastError).HasMaxLength(1_000);
        outbox.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc, entity.OccurredAtUtc });
        outbox.HasIndex(entity => new { entity.TenantId, entity.OccurredAtUtc });
    }
}
