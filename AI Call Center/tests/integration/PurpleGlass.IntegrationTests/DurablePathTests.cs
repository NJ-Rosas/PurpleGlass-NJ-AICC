using Microsoft.EntityFrameworkCore;
using PurpleGlass.Eventing;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;
using PurpleGlass.Modules.Conversation.Infrastructure;

namespace PurpleGlass.IntegrationTests;

[Collection(DurablePathGroup.Name)]
public sealed class DurablePathTests(DurablePathFixture fixture)
{
    [Fact]
    public async Task EmptyDatabaseMigrationsCreateEveryOwnedSchema()
    {
        await using CallManagementDbContext context = fixture.CreateCalls();
        string[] schemas = await context.Database.SqlQueryRaw<string>(
            "SELECT schema_name AS \"Value\" FROM information_schema.schemata WHERE schema_name IN ('eventing','call_management','conversation') ORDER BY schema_name").ToArrayAsync();
        Assert.Equal(["call_management", "conversation", "eventing"], schemas);
    }

    [Fact]
    public async Task InboundRegistrationIsPersistedIdempotentlyWithOutbox()
    {
        Guid tenant = Guid.NewGuid();
        RegisterInboundCall command = Inbound(tenant);
        CallSummary first;
        await using (CallManagementDbContext context = fixture.CreateCalls())
            first = await Calls(context).RegisterInboundAsync(command, default);
        await using (CallManagementDbContext context = fixture.CreateCalls())
        {
            CallSummary replay = await Calls(context).RegisterInboundAsync(command, default);
            Assert.Equal(first.CallId, replay.CallId);
            Assert.Equal(1, await context.Calls.CountAsync(entity => entity.TenantId == new PurpleGlass.Modules.CallManagement.Domain.TenantId(tenant)));
            Assert.Equal(1, await context.OutboxMessages.CountAsync(entity => entity.MessageType == nameof(CallReceived)));
        }
    }

    [Fact]
    public async Task OutboxClaimsAreExclusiveAndExpiredLeasesAreRecoverable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.AddYears(-10);
        OutboxMessage message = OutboxMessage.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"pg/local/v1/tenants/{Guid.NewGuid():D}/events/lease-test",
            "LeaseTest",
            "{}",
            Guid.NewGuid(),
            now);
        await using (EventingDbContext seed = fixture.CreateEventing())
        {
            seed.OutboxMessages.Add(message);
            _ = await seed.SaveChangesAsync();
        }

        Guid firstLease = Guid.NewGuid();
        await using (EventingDbContext firstContext = fixture.CreateEventing())
        {
            var firstStore = new OutboxDispatcherStore(firstContext);
            IReadOnlyList<OutboxMessage> claimed = await firstStore.ClaimBatchAsync(
                firstLease,
                now,
                TimeSpan.FromSeconds(30),
                10,
                default);
            Assert.Contains(claimed, candidate => candidate.Id == message.Id);
        }

        await using (EventingDbContext blockedContext = fixture.CreateEventing())
        {
            var blockedStore = new OutboxDispatcherStore(blockedContext);
            IReadOnlyList<OutboxMessage> blocked = await blockedStore.ClaimBatchAsync(
                Guid.NewGuid(),
                now.AddSeconds(10),
                TimeSpan.FromSeconds(30),
                10,
                default);
            Assert.DoesNotContain(blocked, candidate => candidate.Id == message.Id);
        }

        Guid recoveryLease = Guid.NewGuid();
        await using (EventingDbContext recoveryContext = fixture.CreateEventing())
        {
            var recoveryStore = new OutboxDispatcherStore(recoveryContext);
            IReadOnlyList<OutboxMessage> recovered = await recoveryStore.ClaimBatchAsync(
                recoveryLease,
                now.AddMinutes(1),
                TimeSpan.FromSeconds(30),
                10,
                default);
            OutboxMessage recoveredMessage = Assert.Single(recovered, candidate => candidate.Id == message.Id);
            await recoveryStore.MarkPublishedAsync(
                recoveredMessage,
                recoveryLease,
                now.AddMinutes(1),
                default);
        }

        await using EventingDbContext verify = fixture.CreateEventing();
        OutboxMessage persisted = await verify.OutboxMessages.SingleAsync(candidate => candidate.Id == message.Id);
        Assert.Equal(OutboxMessage.PublishedStatus, persisted.Status);
        Assert.Equal(1, persisted.Attempts);
        Assert.Null(persisted.LeaseId);
    }

    [Fact]
    public async Task InboxExecutesHandlerOnceAndRollsBackFailedHandlers()
    {
        Guid messageId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();
        int executions = 0;

        await using (EventingDbContext firstContext = fixture.CreateEventing())
        {
            var inbox = new InboxDeduplicationStore(firstContext, TimeProvider.System);
            bool processed = await inbox.ExecuteOnceAsync(
                "dashboard-projection",
                messageId,
                tenantId,
                _ =>
                {
                    executions++;
                    return Task.CompletedTask;
                },
                default);
            Assert.True(processed);
        }

        await using (EventingDbContext duplicateContext = fixture.CreateEventing())
        {
            var inbox = new InboxDeduplicationStore(duplicateContext, TimeProvider.System);
            bool processed = await inbox.ExecuteOnceAsync(
                "dashboard-projection",
                messageId,
                tenantId,
                _ =>
                {
                    executions++;
                    return Task.CompletedTask;
                },
                default);
            Assert.False(processed);
        }

        Guid failedMessageId = Guid.NewGuid();
        await using (EventingDbContext failedContext = fixture.CreateEventing())
        {
            var inbox = new InboxDeduplicationStore(failedContext, TimeProvider.System);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inbox.ExecuteOnceAsync(
                    "dashboard-projection",
                    failedMessageId,
                    tenantId,
                    _ => throw new InvalidOperationException("projection failed"),
                    default));
        }

        await using (EventingDbContext retryContext = fixture.CreateEventing())
        {
            var inbox = new InboxDeduplicationStore(retryContext, TimeProvider.System);
            bool retried = await inbox.ExecuteOnceAsync(
                "dashboard-projection",
                failedMessageId,
                tenantId,
                _ => Task.CompletedTask,
                default);
            Assert.True(retried);
        }

        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task OutboundRequestIsPersistedAndIdempotencyConflictIsStable()
    {
        Guid tenant = Guid.NewGuid();
        var command = new RequestOutboundCall(tenant, Guid.NewGuid(), "request-1", "+15550000001", "+15550000002", Guid.NewGuid());
        await using CallManagementDbContext context = fixture.CreateCalls();
        CallManagementService service = Calls(context);
        CallSummary first = await service.RequestOutboundAsync(command, default);
        CallSummary replay = await service.RequestOutboundAsync(command, default);
        Assert.Equal(first.CallId, replay.CallId);
        CallApplicationException conflict = await Assert.ThrowsAsync<CallApplicationException>(() =>
            service.RequestOutboundAsync(command with { ToNumber = "+15550000003" }, default));
        Assert.Equal("idempotency_conflict", conflict.Code);
    }

    [Fact]
    public async Task CallTransitionsPersistAndInvalidTransitionIsRejected()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: false);
        await using CallManagementDbContext context = fixture.CreateCalls();
        CallManagementService service = Calls(context);
        CallSummary answered = await service.MarkAnsweredAsync(new ChangeCallState(tenant, call.CallId, call.Version), default);
        Assert.Equal("Answered", answered.State);
        CallApplicationException error = await Assert.ThrowsAsync<CallApplicationException>(() =>
            service.MarkRingingAsync(new ChangeCallState(tenant, call.CallId, answered.Version), default));
        Assert.Equal("invalid_call_state", error.Code);
    }

    [Fact]
    public async Task ConversationRequiresEligibleSameLocationCallAndIsUniquePerCall()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: true);
        Guid location = await GetCallLocationAsync(tenant, call.CallId);
        await using CallManagementDbContext callContext = fixture.CreateCalls();
        await using ConversationDbContext conversationContext = fixture.CreateConversations();
        var service = new ConversationService(conversationContext, Calls(callContext), TimeProvider.System);
        CreateConversation command = Create(tenant, location, call.CallId);
        ConversationStatusProjection first = await service.CreateAsync(command, default);
        ConversationStatusProjection replay = await service.CreateAsync(command, default);
        Assert.Equal(first.ConversationId, replay.ConversationId);
        Assert.Equal(1, await conversationContext.Conversations.CountAsync());

        (Guid otherTenant, CallSummary ineligible) = await CreateInboundAsync(answer: false);
        Guid otherLocation = await GetCallLocationAsync(otherTenant, ineligible.CallId);
        ConversationApplicationException stateError = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            service.CreateAsync(Create(otherTenant, otherLocation, ineligible.CallId), default));
        Assert.Equal("call_not_eligible", stateError.Code);
        ConversationApplicationException tenantError = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            service.CreateAsync(Create(tenant, Guid.NewGuid(), call.CallId), default));
        Assert.Equal("idempotency_conflict", tenantError.Code);

        (Guid mismatchTenant, CallSummary mismatchCall) = await CreateInboundAsync(answer: true);
        ConversationApplicationException locationError = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            service.CreateAsync(Create(mismatchTenant, Guid.NewGuid(), mismatchCall.CallId), default));
        Assert.Equal("tenant_mismatch", locationError.Code);
    }

    [Fact]
    public async Task TurnsAreDeterministicIdempotentAndSummaryPersists()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: true);
        Guid location = await GetCallLocationAsync(tenant, call.CallId);
        await using CallManagementDbContext callContext = fixture.CreateCalls();
        await using ConversationDbContext context = fixture.CreateConversations();
        ConversationService service = new(context, Calls(callContext), TimeProvider.System);
        ConversationStatusProjection created = await service.CreateAsync(Create(tenant, location, call.CallId), default);
        ConversationStatusProjection active = await service.ActivateAsync(new ChangeConversationState(tenant, created.ConversationId, created.Version), default);
        Guid callerTurn = Guid.NewGuid();
        LiveTranscriptTurn caller = await service.AddCallerTurnAsync(new AddConversationTurn(tenant, created.ConversationId, active.Version, callerTurn, "My name is Maya", 0.95m), default);
        LiveTranscriptTurn replay = await service.AddCallerTurnAsync(new AddConversationTurn(tenant, created.ConversationId, active.Version, callerTurn, "My name is Maya", 0.95m), default);
        Assert.Equal(caller.TurnId, replay.TurnId);
        LiveTranscriptTurn assistant = await service.AddAssistantTurnAsync(new AddConversationTurn(tenant, created.ConversationId, active.Version + 1, Guid.NewGuid(), "How may I help?"), default);
        Assert.Equal(2, assistant.SequenceNumber);
        var summary = new ConversationSummary("Caller introduced herself.", "General inquiry", "Captured", false, false, DateTimeOffset.UtcNow, "demo-v1");
        CompletedConversationSummary completed = await service.CompleteAsync(new CompleteConversation(tenant, created.ConversationId, active.Version + 2, summary), default);
        CompletedConversationSummary completionReplay = await service.CompleteAsync(new CompleteConversation(tenant, created.ConversationId, active.Version + 2, summary), default);
        Assert.Equal("Captured", completed.Outcome);
        Assert.Equal(completed.ConversationId, completionReplay.ConversationId);
        Assert.Equal(2, (await service.GetTranscriptAsync(tenant, created.ConversationId, default)).Count);
        CompletedConversationSummary persisted = await service.GetSummaryAsync(tenant, created.ConversationId, default);
        Assert.Equal(completed.ConversationId, persisted.ConversationId);
        Assert.Equal(completed.Outcome, persisted.Outcome);
        Assert.Equal(completed.Summary, persisted.Summary);
    }

    [Fact]
    public async Task DuplicateTurnWithDifferentContentIsRejected()
    {
        (ConversationService service, Guid tenant, ConversationStatusProjection active) = await CreateActiveConversationAsync();
        Guid turnId = Guid.NewGuid();
        _ = await service.AddCallerTurnAsync(new AddConversationTurn(tenant, active.ConversationId, active.Version, turnId, "First"), default);
        ConversationApplicationException error = await Assert.ThrowsAsync<ConversationApplicationException>(() =>
            service.AddCallerTurnAsync(new AddConversationTurn(tenant, active.ConversationId, active.Version, turnId, "Different"), default));
        Assert.Equal("idempotency_conflict", error.Code);
    }

    [Fact]
    public async Task BusinessStateAndOutboxRollbackTogether()
    {
        Guid tenant = Guid.NewGuid();
        RegisterInboundCall command = Inbound(tenant);
        Guid callId;
        await using (CallManagementDbContext context = fixture.CreateCalls())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            callId = (await Calls(context).RegisterInboundAsync(command, default)).CallId;
            await transaction.RollbackAsync();
        }
        await using CallManagementDbContext verify = fixture.CreateCalls();
        Assert.False(await verify.Calls.AnyAsync(entity => entity.Id == new CallSessionId(callId)));
        Assert.False(await verify.OutboxMessages.AnyAsync(entity => entity.CorrelationId == command.CorrelationId));
    }

    [Fact]
    public async Task StaleTrackedUpdateIsRejectedByDatabaseConcurrencyToken()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: false);
        await using CallManagementDbContext first = fixture.CreateCalls();
        await using CallManagementDbContext stale = fixture.CreateCalls();
        CallSession firstCall = (await ((ICallStore)first).GetAsync(tenant, call.CallId, true, default))!;
        CallSession staleCall = (await ((ICallStore)stale).GetAsync(tenant, call.CallId, true, default))!;
        firstCall.Answer(DateTimeOffset.UtcNow);
        _ = await first.SaveChangesAsync();
        staleCall.Answer(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [Fact]
    public async Task RecordingMetadataAndTenantScopedQueriesPersist()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: true);
        await using CallManagementDbContext context = fixture.CreateCalls();
        CallManagementService service = Calls(context);
        CallSummary completed = await service.CompleteAsync(new CompleteCall(tenant, call.CallId, call.Version, "Answered"), default);
        CallSummary completionReplay = await service.CompleteAsync(new CompleteCall(tenant, call.CallId, call.Version, "Answered"), default);
        Assert.Equal(completed.Version, completionReplay.Version);
        var recording = new RecordingMetadata("mock-storage", "calls/opaque-1", "audio/wav", 1200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), "sha256:test", 4096);
        CallSummary updated = await service.AttachRecordingAsync(new AttachCallRecording(tenant, call.CallId, completed.Version, recording), default);
        Assert.Equal("calls/opaque-1", updated.RecordingReference);
        Assert.Single(await service.GetRecentAsync(tenant, null, 10, default));
        Assert.Equal(call.CallId, (await service.GetByProviderCallIdAsync(tenant, (await context.Calls.AsNoTracking().SingleAsync(entity => entity.Id == new CallSessionId(call.CallId))).ProviderCallId!, default)).CallId);
    }

    private async Task<(Guid Tenant, CallSummary Call)> CreateInboundAsync(bool answer)
    {
        Guid tenant = Guid.NewGuid();
        await using CallManagementDbContext context = fixture.CreateCalls();
        CallManagementService service = Calls(context);
        CallSummary call = await service.RegisterInboundAsync(Inbound(tenant), default);
        if (answer) call = await service.MarkAnsweredAsync(new ChangeCallState(tenant, call.CallId, call.Version), default);
        return (tenant, call);
    }

    private async Task<Guid> GetCallLocationAsync(Guid tenant, Guid callId)
    {
        await using CallManagementDbContext context = fixture.CreateCalls();
        CallEligibility eligibility = (await Calls(context).GetEligibilityAsync(tenant, callId, default))!;
        return eligibility.LocationId;
    }

    private async Task<(ConversationService Service, Guid Tenant, ConversationStatusProjection Active)> CreateActiveConversationAsync()
    {
        (Guid tenant, CallSummary call) = await CreateInboundAsync(answer: true);
        Guid location = await GetCallLocationAsync(tenant, call.CallId);
        CallManagementDbContext callContext = fixture.CreateCalls();
        ConversationDbContext conversationContext = fixture.CreateConversations();
        var service = new ConversationService(conversationContext, Calls(callContext), TimeProvider.System);
        ConversationStatusProjection created = await service.CreateAsync(Create(tenant, location, call.CallId), default);
        ConversationStatusProjection active = await service.ActivateAsync(new ChangeConversationState(tenant, created.ConversationId, created.Version), default);
        return (service, tenant, active);
    }

    private static RegisterInboundCall Inbound(Guid tenant) => new(tenant, Guid.NewGuid(), $"provider-{Guid.NewGuid():N}", "+15550000002", "+15550000001", Guid.NewGuid());
    private static CreateConversation Create(Guid tenant, Guid location, Guid callId) => new(tenant, location, callId, Guid.NewGuid(), "demo-v1", "en-US");
    private static CallManagementService Calls(CallManagementDbContext context) => new(context, TimeProvider.System);
}
