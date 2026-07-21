using System.Text.Json;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;
using ConversationAggregate = PurpleGlass.Modules.Conversation.Domain.Conversation;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed class ConversationService(IConversationStore store, ICallEligibilityQuery callEligibility, TimeProvider timeProvider)
{
    public async Task<ConversationStatusProjection> CreateAsync(CreateConversation command, CancellationToken cancellationToken)
    {
        ConversationAggregate? existing = await store.GetForCallAsync(command.TenantId, command.CallId, false, cancellationToken);
        if (existing is not null)
        {
            if (existing.LocationId.Value != command.LocationId || existing.ConfigurationVersion != command.ConfigurationVersion || existing.Language != command.Language.Trim())
                throw ConversationApplicationException.IdempotencyConflict();
            return Status(existing);
        }

        CallEligibility eligibility = await callEligibility.GetEligibilityAsync(command.TenantId, command.CallId, cancellationToken)
            ?? throw ConversationApplicationException.CallNotFound();
        if (eligibility.TenantId != command.TenantId || eligibility.LocationId != command.LocationId)
            throw ConversationApplicationException.TenantMismatch();
        if (!eligibility.ConversationAllowed) throw ConversationApplicationException.CallNotEligible();

        var conversation = new ConversationAggregate(
            ConversationId.New(), new CallSessionReference(command.CallId), new TenantId(command.TenantId),
            new LocationId(command.LocationId), new CorrelationId(command.CorrelationId),
            command.ConfigurationVersion, command.Language, timeProvider.GetUtcNow());
        store.Add(conversation);
        await SaveAsync(cancellationToken);
        return Status(conversation);
    }

    public async Task<ConversationStatusProjection> ActivateAsync(ChangeConversationState command, CancellationToken cancellationToken)
    {
        ConversationAggregate conversation = await Load(command.TenantId, command.ConversationId, cancellationToken);
        if (conversation.State == ConversationState.Active) return Status(conversation);
        EnsureVersion(conversation, command.ExpectedVersion);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => conversation.Activate(now));
        AddEvent(conversation, new ConversationStarted(conversation.Id.Value, conversation.CallSession.Value,
            conversation.Language, conversation.ConfigurationVersion, now), nameof(ConversationStarted), now);
        await SaveAsync(cancellationToken);
        return Status(conversation);
    }

    public Task<LiveTranscriptTurn> AddCallerTurnAsync(AddConversationTurn command, CancellationToken cancellationToken) =>
        AddTurnAsync(command, SpeakerRole.Caller, cancellationToken);

    public Task<LiveTranscriptTurn> AddAssistantTurnAsync(AddConversationTurn command, CancellationToken cancellationToken) =>
        AddTurnAsync(command, SpeakerRole.Assistant, cancellationToken);

    public async Task<ConversationStatusProjection> RecordEscalationAsync(RecordConversationEscalation command, CancellationToken cancellationToken)
    {
        ConversationAggregate conversation = await LoadForChange(command.TenantId, command.ConversationId, command.ExpectedVersion, cancellationToken);
        if (conversation.Escalated && conversation.EscalationReason == command.Reason.Trim()) return Status(conversation);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => conversation.RecordEscalation(command.Reason));
        AddEvent(conversation, new EscalationTriggered(conversation.Id.Value, conversation.EscalationReason!, now), nameof(EscalationTriggered), now);
        await SaveAsync(cancellationToken);
        return Status(conversation);
    }

    public async Task<CompletedConversationSummary> CompleteAsync(CompleteConversation command, CancellationToken cancellationToken)
    {
        ConversationAggregate conversation = await store.GetAsync(command.TenantId, command.ConversationId, true, cancellationToken)
            ?? throw ConversationApplicationException.NotFound();
        if (conversation.State == ConversationState.Completed && conversation.Summary == command.Summary) return Completed(conversation);
        EnsureVersion(conversation, command.ExpectedVersion);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => conversation.Complete(command.Summary, now));
        AddEvent(conversation, new SummaryGenerated(conversation.Id.Value, command.Summary.Text, command.Summary.CallerIntent,
            command.Summary.Outcome, command.Summary.FollowUpRequired, command.Summary.Escalated,
            command.Summary.ConfigurationVersion, command.Summary.GeneratedAtUtc), nameof(SummaryGenerated), now);
        AddEvent(conversation, new ConversationCompleted(conversation.Id.Value, now), nameof(ConversationCompleted), now);
        await SaveAsync(cancellationToken);
        return Completed(conversation);
    }

    public async Task<ConversationStatusProjection> FailAsync(ChangeConversationState command, CancellationToken cancellationToken)
    {
        ConversationAggregate conversation = await Load(command.TenantId, command.ConversationId, cancellationToken);
        if (conversation.State == ConversationState.Failed) return Status(conversation);
        EnsureVersion(conversation, command.ExpectedVersion);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Apply(() => conversation.Fail(now));
        AddEvent(conversation, new ConversationFailed(conversation.Id.Value, now), nameof(ConversationFailed), now);
        await SaveAsync(cancellationToken);
        return Status(conversation);
    }

    public async Task<ConversationStatusProjection> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Status(await store.GetAsync(tenantId, conversationId, false, cancellationToken) ?? throw ConversationApplicationException.NotFound());
    public async Task<ConversationStatusProjection> GetForCallAsync(Guid tenantId, Guid callId, CancellationToken cancellationToken) =>
        Status(await store.GetForCallAsync(tenantId, callId, false, cancellationToken) ?? throw ConversationApplicationException.NotFound());
    public async Task<IReadOnlyList<LiveTranscriptTurn>> GetTranscriptAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        MapTranscript(await store.GetAsync(tenantId, conversationId, false, cancellationToken) ?? throw ConversationApplicationException.NotFound());
    public async Task<CompletedConversationSummary> GetSummaryAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Completed(await store.GetAsync(tenantId, conversationId, false, cancellationToken) ?? throw ConversationApplicationException.NotFound());

    private async Task<LiveTranscriptTurn> AddTurnAsync(AddConversationTurn command, SpeakerRole speaker, CancellationToken cancellationToken)
    {
        ConversationAggregate conversation = await store.GetAsync(command.TenantId, command.ConversationId, true, cancellationToken)
            ?? throw ConversationApplicationException.NotFound();
        ConversationTurn? existing = conversation.Turns.SingleOrDefault(turn => turn.Id.Value == command.TurnId);
        if (existing is not null)
        {
            if (existing.Text != command.Text.Trim() || existing.Speaker != speaker) throw ConversationApplicationException.IdempotencyConflict();
            return Turn(existing, conversation.CallSession.Value);
        }
        EnsureVersion(conversation, command.ExpectedVersion);
        DateTimeOffset now = timeProvider.GetUtcNow();
        ConversationTurn turn;
        try
        {
            turn = conversation.AddTurn(new ConversationTurnId(command.TurnId), speaker, command.Text, now,
            recognitionConfidence: command.RecognitionConfidence, safetyFlagged: command.SafetyFlagged, escalationFlagged: command.EscalationFlagged);
        }
        catch (InvalidOperationException exception) { throw ConversationApplicationException.InvalidState(exception); }
        object payload = speaker == SpeakerRole.Caller
            ? new UserSpeechRecognized(conversation.Id.Value, turn.Id.Value, turn.SequenceNumber, turn.Text, turn.RecognitionConfidence, now)
            : new AIResponseGenerated(conversation.Id.Value, turn.Id.Value, turn.SequenceNumber, turn.Text, conversation.ConfigurationVersion, now);
        AddEvent(conversation, payload, payload.GetType().Name, now);
        await SaveAsync(cancellationToken);
        return Turn(turn, conversation.CallSession.Value);
    }

    private Task<ConversationAggregate> LoadForChange(ChangeConversationState command, CancellationToken cancellationToken) =>
        LoadForChange(command.TenantId, command.ConversationId, command.ExpectedVersion, cancellationToken);
    private async Task<ConversationAggregate> LoadForChange(Guid tenantId, Guid id, long expected, CancellationToken cancellationToken)
    { ConversationAggregate conversation = await store.GetAsync(tenantId, id, true, cancellationToken) ?? throw ConversationApplicationException.NotFound(); EnsureVersion(conversation, expected); return conversation; }
    private static void EnsureVersion(ConversationAggregate conversation, long expected) { if (conversation.Version != expected) throw ConversationApplicationException.Concurrency(); }
    private void AddEvent<T>(ConversationAggregate conversation, T payload, string type, DateTimeOffset now) => store.AddOutbox(OutboxMessage.Create(
        conversation.TenantId.Value, conversation.LocationId.Value,
        $"pg/local/v1/tenants/{conversation.TenantId.Value:D}/calls/{conversation.CallSession.Value:D}/events/{ToTopic(type)}",
        type, JsonSerializer.Serialize(payload), conversation.CorrelationId.Value, now, producer: "conversation"));
    private async Task SaveAsync(CancellationToken cancellationToken) { try { await store.SaveChangesAsync(cancellationToken); } catch (ConversationPersistenceConcurrencyException e) { throw ConversationApplicationException.Concurrency(e); } }
    private static void Apply(Action action) { try { action(); } catch (InvalidOperationException e) { throw ConversationApplicationException.InvalidState(e); } }
    private static string ToTopic(string value) => string.Concat(value.Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
    private static ConversationStatusProjection Status(ConversationAggregate c) => new(c.Id.Value, c.CallSession.Value, c.State.ToString(), c.Language, c.Escalated, c.Version);
    private static LiveTranscriptTurn Turn(ConversationTurn t, Guid callId = default) => new(t.ConversationId.Value, callId, t.Id.Value, t.Speaker.ToString(), t.SequenceNumber, t.Text, t.CreatedAtUtc, t.SafetyFlagged, t.EscalationFlagged);
    private static LiveTranscriptTurn[] MapTranscript(ConversationAggregate c) => c.Turns.Select(t => Turn(t, c.CallSession.Value)).ToArray();
    private static CompletedConversationSummary Completed(ConversationAggregate c) { ConversationSummary s = c.Summary ?? throw ConversationApplicationException.InvalidState(new InvalidOperationException()); return new(c.Id.Value, c.CallSession.Value, s.Text, s.CallerIntent, s.Outcome, s.FollowUpRequired, s.Escalated, s.GeneratedAtUtc); }

    private async Task<ConversationAggregate> Load(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(tenantId, id, true, cancellationToken) ?? throw ConversationApplicationException.NotFound();
}
