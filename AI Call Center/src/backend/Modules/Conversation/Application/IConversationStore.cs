using PurpleGlass.Eventing;
using PurpleGlass.Modules.Conversation.Domain;
using ConversationAggregate = PurpleGlass.Modules.Conversation.Domain.Conversation;

namespace PurpleGlass.Modules.Conversation.Application;

public interface IConversationStore
{
    Task<ConversationAggregate?> GetAsync(Guid tenantId, Guid conversationId, bool tracking, CancellationToken cancellationToken);
    Task<ConversationAggregate?> GetForCallAsync(Guid tenantId, Guid callId, bool tracking, CancellationToken cancellationToken);
    void Add(ConversationAggregate conversation);
    void AddOutbox(OutboxMessage message);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
