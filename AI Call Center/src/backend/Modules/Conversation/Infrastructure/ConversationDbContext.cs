using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Domain;
using ConversationAggregate = PurpleGlass.Modules.Conversation.Domain.Conversation;

namespace PurpleGlass.Modules.Conversation.Infrastructure;

public sealed class ConversationDbContext(DbContextOptions<ConversationDbContext> options)
    : DbContext(options), IConversationStore
{
    public DbSet<ConversationAggregate> Conversations => Set<ConversationAggregate>();
    public DbSet<ConversationTurn> Turns => Set<ConversationTurn>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public async Task<ConversationAggregate?> GetAsync(Guid tenantId, Guid conversationId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<ConversationAggregate> query = Conversations.Include(entity => entity.Turns);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(entity => entity.TenantId == new TenantId(tenantId)
            && entity.Id == new ConversationId(conversationId), cancellationToken);
    }

    public async Task<ConversationAggregate?> GetForCallAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<ConversationAggregate> query = Conversations.Include(entity => entity.Turns);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(entity => entity.TenantId == new TenantId(tenantId)
            && entity.CallSession == new CallSessionReference(callId), cancellationToken);
    }

    public void Add(ConversationAggregate conversation) => Conversations.Add(conversation);
    public void AddOutbox(OutboxMessage message) => OutboxMessages.Add(message);
    async Task IConversationStore.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try { _ = await SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new ConversationPersistenceConcurrencyException(exception); }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConversation(modelBuilder.Entity<ConversationAggregate>());
        ConfigureTurn(modelBuilder.Entity<ConversationTurn>());
        ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }

    private static void ConfigureConversation(EntityTypeBuilder<ConversationAggregate> conversation)
    {
        conversation.ToTable("conversations", "conversation");
        conversation.HasKey(entity => entity.Id);
        conversation.Property(entity => entity.Id).HasConversion(id => id.Value, value => new ConversationId(value));
        conversation.Property(entity => entity.CallSession).HasConversion(id => id.Value, value => new CallSessionReference(value));
        conversation.Property(entity => entity.TenantId).HasConversion(id => id.Value, value => new TenantId(value));
        conversation.Property(entity => entity.LocationId).HasConversion(id => id.Value, value => new LocationId(value));
        conversation.Property(entity => entity.CorrelationId).HasConversion(id => id.Value, value => new CorrelationId(value));
        conversation.Property(entity => entity.ConfigurationVersion).HasMaxLength(100).IsRequired();
        conversation.Property(entity => entity.Language).HasMaxLength(35).IsRequired();
        conversation.Property(entity => entity.EscalationReason).HasMaxLength(500);
        conversation.Property(entity => entity.Version).IsConcurrencyToken();
        conversation.OwnsOne(entity => entity.Summary, summary =>
        {
            summary.Property(value => value.Text).HasColumnName("SummaryText").HasMaxLength(4_000);
            summary.Property(value => value.CallerIntent).HasColumnName("SummaryCallerIntent").HasMaxLength(200);
            summary.Property(value => value.Outcome).HasColumnName("SummaryOutcome").HasMaxLength(200);
            summary.Property(value => value.FollowUpRequired).HasColumnName("SummaryFollowUpRequired");
            summary.Property(value => value.Escalated).HasColumnName("SummaryEscalated");
            summary.Property(value => value.GeneratedAtUtc).HasColumnName("SummaryGeneratedAtUtc");
            summary.Property(value => value.ConfigurationVersion).HasColumnName("SummaryConfigurationVersion").HasMaxLength(100);
        });
        conversation.HasMany(entity => entity.Turns).WithOne().HasForeignKey(entity => entity.ConversationId).OnDelete(DeleteBehavior.Cascade);
        conversation.Navigation(entity => entity.Turns).UsePropertyAccessMode(PropertyAccessMode.Field);
        conversation.HasIndex(entity => new { entity.TenantId, entity.CallSession }).IsUnique();
        conversation.HasIndex(entity => new { entity.TenantId, entity.LocationId, entity.CreatedAtUtc });
    }

    private static void ConfigureTurn(EntityTypeBuilder<ConversationTurn> turn)
    {
        turn.ToTable("conversation_turns", "conversation");
        turn.HasKey(entity => entity.Id);
        turn.Property(entity => entity.Id).HasConversion(id => id.Value, value => new ConversationTurnId(value));
        turn.Property(entity => entity.ConversationId).HasConversion(id => id.Value, value => new ConversationId(value));
        turn.Property(entity => entity.Text).HasMaxLength(8_000).IsRequired();
        turn.Property(entity => entity.RecognitionConfidence).HasPrecision(5, 4);
        turn.HasIndex(entity => new { entity.ConversationId, entity.SequenceNumber }).IsUnique();
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
