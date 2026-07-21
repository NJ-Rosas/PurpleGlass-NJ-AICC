using PurpleGlass.Modules.Conversation.Domain;

namespace PurpleGlass.UnitTests;

public sealed class ConversationTests
{
    [Fact]
    public void ConversationRetainsTenantLocationCallAndCorrelationAssociation()
    {
        ConversationFixture fixture = CreateFixture();

        Assert.Equal(fixture.TenantId, fixture.Conversation.TenantId);
        Assert.Equal(fixture.LocationId, fixture.Conversation.LocationId);
        Assert.Equal(fixture.CallSession, fixture.Conversation.CallSession);
        Assert.Equal(fixture.CorrelationId, fixture.Conversation.CorrelationId);
        Assert.Equal(ConversationState.Created, fixture.Conversation.State);
    }

    [Fact]
    public void ConversationCanBeActivatedAndAddsDeterministicallySequencedTurns()
    {
        Conversation conversation = CreateFixture().Conversation;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        conversation.Activate(now);

        ConversationTurn caller = conversation.AddTurn(
            ConversationTurnId.New(),
            SpeakerRole.Caller,
            "My name is Maya and I need the office hours.",
            now.AddSeconds(1),
            recognitionConfidence: 0.94m);
        ConversationTurn assistant = conversation.AddTurn(
            ConversationTurnId.New(),
            SpeakerRole.Assistant,
            "The office is open from eight to five.",
            now.AddSeconds(2));

        Assert.Equal(ConversationState.Active, conversation.State);
        Assert.Equal(1, caller.SequenceNumber);
        Assert.Equal(2, assistant.SequenceNumber);
        Assert.Equal([caller, assistant], conversation.Turns);
    }

    [Fact]
    public void TurnCannotBeAddedBeforeActivation()
    {
        Conversation conversation = CreateFixture().Conversation;

        _ = Assert.Throws<InvalidOperationException>(() => conversation.AddTurn(
            ConversationTurnId.New(),
            SpeakerRole.Caller,
            "Hello",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TurnCannotBeAddedAfterCompletion()
    {
        Conversation conversation = CreateFixture().Conversation;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        conversation.Activate(now);
        conversation.Complete(CreateSummary(escalated: false, now.AddSeconds(1)), now.AddSeconds(2));

        _ = Assert.Throws<InvalidOperationException>(() => conversation.AddTurn(
            ConversationTurnId.New(),
            SpeakerRole.Caller,
            "One more question",
            now.AddSeconds(3)));
    }

    [Fact]
    public void ConversationCompletesWithMatchingFinalSummary()
    {
        Conversation conversation = CreateFixture().Conversation;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        conversation.Activate(now);
        ConversationSummary summary = CreateSummary(escalated: false, now.AddMinutes(1));

        conversation.Complete(summary, now.AddMinutes(1));

        Assert.Equal(ConversationState.Completed, conversation.State);
        Assert.Same(summary, conversation.Summary);
        ConversationSummary completedSummary = Assert.IsType<ConversationSummary>(conversation.Summary);
        Assert.Equal("Office hours", completedSummary.CallerIntent);
    }

    [Fact]
    public void EscalationIsRecordedAndMustMatchSummary()
    {
        Conversation conversation = CreateFixture().Conversation;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        conversation.Activate(now);

        conversation.RecordEscalation("Caller requested staff assistance");
        conversation.Complete(CreateSummary(escalated: true, now.AddMinutes(1)), now.AddMinutes(1));

        Assert.True(conversation.Escalated);
        Assert.Equal("Caller requested staff assistance", conversation.EscalationReason);
        Assert.True(conversation.Summary?.Escalated);
    }

    [Fact]
    public void InvalidLifecycleTransitionsAreRejected()
    {
        Conversation conversation = CreateFixture().Conversation;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        conversation.Activate(now);

        _ = Assert.Throws<InvalidOperationException>(() => conversation.Activate(now.AddSeconds(1)));
        conversation.Fail(now.AddSeconds(2));
        _ = Assert.Throws<InvalidOperationException>(() => conversation.Fail(now.AddSeconds(3)));
    }

    [Fact]
    public void DuplicateTurnIdentifierIsRejected()
    {
        Conversation conversation = CreateFixture().Conversation;
        conversation.Activate(DateTimeOffset.UtcNow);
        ConversationTurnId turnId = ConversationTurnId.New();
        _ = conversation.AddTurn(turnId, SpeakerRole.Caller, "Hello", DateTimeOffset.UtcNow);

        _ = Assert.Throws<InvalidOperationException>(() =>
            conversation.AddTurn(turnId, SpeakerRole.Assistant, "Hello back", DateTimeOffset.UtcNow));
    }

    private static ConversationFixture CreateFixture()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var locationId = new LocationId(Guid.NewGuid());
        var callSession = new CallSessionReference(Guid.NewGuid());
        var correlationId = new CorrelationId(Guid.NewGuid());
        var conversation = new Conversation(
            ConversationId.New(),
            callSession,
            tenantId,
            locationId,
            correlationId,
            "demo-config-v1",
            "en-US",
            DateTimeOffset.UtcNow);

        return new ConversationFixture(conversation, tenantId, locationId, callSession, correlationId);
    }

    private static ConversationSummary CreateSummary(bool escalated, DateTimeOffset generatedAtUtc) => new(
        "The caller requested office hours and received an answer.",
        "Office hours",
        "Answered",
        false,
        escalated,
        generatedAtUtc,
        "demo-config-v1");

    private sealed record ConversationFixture(
        Conversation Conversation,
        TenantId TenantId,
        LocationId LocationId,
        CallSessionReference CallSession,
        CorrelationId CorrelationId);
}
