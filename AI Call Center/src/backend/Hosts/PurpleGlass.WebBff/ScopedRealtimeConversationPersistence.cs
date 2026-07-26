using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;

namespace PurpleGlass.WebBff;

public sealed class ScopedRealtimeConversationPersistence(IServiceScopeFactory scopeFactory)
    : IRealtimeConversationPersistence
{
    public Task<ConversationStatusProjection> CreateAsync(
        CreateConversation command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_create", service => service.CreateAsync(command, cancellationToken));

    public Task<ConversationStatusProjection> ActivateAsync(
        ChangeConversationState command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_activate", service => service.ActivateAsync(command, cancellationToken));

    public Task<ConversationStatusProjection> GetAsync(
        Guid tenantId,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_read", service => service.GetAsync(tenantId, conversationId, cancellationToken));

    public Task<IReadOnlyList<LiveTranscriptTurn>> GetTranscriptAsync(
        Guid tenantId,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_transcript_read",
            service => service.GetTranscriptAsync(tenantId, conversationId, cancellationToken));

    public Task<LiveTranscriptTurn> AddCallerTurnAsync(
        AddConversationTurn command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("caller_turn_persist", service => service.AddCallerTurnAsync(command, cancellationToken));

    public Task<LiveTranscriptTurn> AddAssistantTurnAsync(
        AddConversationTurn command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("assistant_turn_persist", service => service.AddAssistantTurnAsync(command, cancellationToken));

    public Task<CompletedConversationSummary> CompleteAsync(
        CompleteConversation command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_complete", service => service.CompleteAsync(command, cancellationToken));

    public Task<ConversationStatusProjection> FailAsync(
        ChangeConversationState command,
        CancellationToken cancellationToken) =>
        ExecuteAsync("conversation_fail", service => service.FailAsync(command, cancellationToken));

    private async Task<T> ExecuteAsync<T>(string stage, Func<ConversationService, Task<T>> operation)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConversationService service = scope.ServiceProvider.GetRequiredService<ConversationService>();
        try
        {
            return await operation(service);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConversationApplicationException exception)
        {
            string code = exception.Code switch
            {
                "concurrency_conflict" => "voice_persistence_conflict",
                "invalid_conversation_state" or "idempotency_conflict" => "conversation_state_conflict",
                _ => "voice_persistence_failed",
            };
            throw new VoicePersistenceException(code, stage, exception);
        }
        catch (Exception exception)
        {
            throw new VoicePersistenceException("voice_persistence_failed", stage, exception);
        }
    }
}
