using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed class EventingDbContext(DbContextOptions<EventingDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Configure(modelBuilder.Entity<OutboxMessage>());
        Configure(modelBuilder.Entity<InboxMessage>());
    }

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
        outbox.Property(entity => entity.TraceParent).HasMaxLength(100);
        outbox.Property(entity => entity.TraceState).HasMaxLength(512);
        outbox.Property(entity => entity.LastError).HasMaxLength(1_000);
        outbox.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc, entity.LeaseExpiresAtUtc, entity.OccurredAtUtc });
        outbox.HasIndex(entity => new { entity.TenantId, entity.OccurredAtUtc });
    }

    public static void Configure(EntityTypeBuilder<InboxMessage> inbox)
    {
        inbox.ToTable("inbox_messages", "eventing");
        inbox.HasKey(entity => entity.Id);
        inbox.Property(entity => entity.ConsumerName).HasMaxLength(150).IsRequired();
        inbox.HasIndex(entity => new { entity.ConsumerName, entity.MessageId }).IsUnique();
        inbox.HasIndex(entity => new { entity.TenantId, entity.ReceivedAtUtc });
    }
}
