using Microsoft.EntityFrameworkCore;
using PurpleGlass.Adapters.PracticeManagement.Deterministic;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Modules.Scheduling.Application;
using PurpleGlass.Modules.Scheduling.Domain;
using PurpleGlass.Modules.Scheduling.Infrastructure;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Scheduling.Contracts;
namespace PurpleGlass.IntegrationTests;
[Collection(DurablePathGroup.Name)]
public sealed class SchedulingDurableTests(DurablePathFixture fixture)
{
    private static readonly DateTimeOffset Now=new(2026,8,5,12,0,0,TimeSpan.Zero);
    [Fact] public async Task SearchBookAndExactReplayPersistOneAtomicWorkflowAndOutbox()
    {
        await ResetAsync();
        var ids=Scope();var fake=new DeterministicPracticeManagementSystem(new FixedClock(Now));var accessor=Accessor(ids);string token;Guid workflowId;
        await using(SchedulingDbContext db=fixture.CreateScheduling())
        {var service=new SchedulingService(db,fake,accessor,new FixedClock(Now));var offers=await service.SearchAvailabilityAsync(Search(ids.Location),default);token=offers[0].OfferToken;var accepted=await service.BookAppointmentAsync(new(ids.Location,token,"synthetic-party","exam","idem-1",1),default);workflowId=accepted.WorkflowId;var replay=await service.BookAppointmentAsync(new(ids.Location,token,"synthetic-party","exam","idem-1",1),default);Assert.Equal(workflowId,replay.WorkflowId);}
        await using SchedulingDbContext verify=fixture.CreateScheduling();Assert.Equal(1,await verify.AppointmentWorkflows.CountAsync(x=>x.Id==workflowId));Assert.Equal(1,await verify.ProviderOperations.CountAsync(x=>x.WorkflowId==workflowId));Assert.Equal(1,await verify.IdempotencyReceipts.CountAsync(x=>x.WorkflowId==workflowId));Assert.True(await verify.OutboxMessages.CountAsync(x=>x.CorrelationId==ids.Correlation)>=2);Assert.Equal(AvailabilityOfferState.Consumed,(await verify.AvailabilityOffers.SingleAsync(x=>x.ConsumedByWorkflowId==workflowId)).State);
    }
    [Fact] public async Task ChangedDuplicateConflictsAndDistinctRequestCannotReuseOffer()
    {
        await ResetAsync();
        var ids=Scope();var accessor=Accessor(ids);var fake=new DeterministicPracticeManagementSystem(new FixedClock(Now));await using SchedulingDbContext db=fixture.CreateScheduling();var service=new SchedulingService(db,fake,accessor,new FixedClock(Now));string token=(await service.SearchAvailabilityAsync(Search(ids.Location),default))[0].OfferToken;await service.BookAppointmentAsync(new(ids.Location,token,"party-a","exam","idem-2",1),default);SchedulingApplicationException mismatch=await Assert.ThrowsAsync<SchedulingApplicationException>(()=>service.BookAppointmentAsync(new(ids.Location,token,"party-b","exam","idem-2",1),default));Assert.Equal("idempotency_conflict",mismatch.Code);SchedulingApplicationException consumed=await Assert.ThrowsAsync<SchedulingApplicationException>(()=>service.BookAppointmentAsync(new(ids.Location,token,"party-a","exam","idem-3",2),default));Assert.Equal("availability_conflict",consumed.Code);
    }
    [Fact] public async Task UnknownResultReconcilesWithoutSecondMutation()
    {
        await ResetAsync();
        var ids=Scope();var accessor=Accessor(ids);var fake=new DeterministicPracticeManagementSystem(new FixedClock(Now)){BookingScenario=DeterministicBookingScenario.UnknownAfterAcceptance};Guid workflow;
        await using(SchedulingDbContext db=fixture.CreateScheduling()){var s=new SchedulingService(db,fake,accessor,new FixedClock(Now));string token=(await s.SearchAvailabilityAsync(Search(ids.Location),default))[0].OfferToken;workflow=(await s.BookAppointmentAsync(new(ids.Location,token,"party","exam","idem-4",1),default)).WorkflowId;SchedulingDispatch book=(await s.BeginNextDispatchAsync(default))!;ProviderBookingResult result=await fake.BookAppointmentAsync(new(book.OperationId,book.WorkflowId,book.TenantId,book.LocationId,book.SlotReference,book.PartyReference,book.AppointmentTypeCode,book.StartUtc,book.EndUtc,book.OfficeTimeZone,book.SourceVersion),default);await s.ApplyBookingResultAsync(book.OperationId,result,default);SchedulingDispatch reconcile=(await s.BeginNextDispatchAsync(default))!;ProviderReconciliationResult resolution=await fake.ReconcileBookingAsync(new(reconcile.OperationId,reconcile.WorkflowId,reconcile.TenantId,reconcile.LocationId),default);await s.ApplyReconciliationResultAsync(reconcile.OperationId,resolution,default);Assert.Equal("Confirmed",(await s.GetStatusAsync(ids.Location,workflow,default)).State);}
        Assert.Equal(1,fake.BookingInvocations);Assert.Equal(1,fake.ReconciliationInvocations);
    }
    [Fact] public async Task DispatchedCrashRecoversAsReconciliationWithoutBookingReplay()
    {
        await ResetAsync();
        var ids=Scope();var accessor=Accessor(ids);var fake=new DeterministicPracticeManagementSystem(new FixedClock(Now));Guid workflow;
        await using(SchedulingDbContext db=fixture.CreateScheduling()){var s=new SchedulingService(db,fake,accessor,new FixedClock(Now));string token=(await s.SearchAvailabilityAsync(Search(ids.Location),default))[0].OfferToken;workflow=(await s.BookAppointmentAsync(new(ids.Location,token,"party","exam","idem-5",1),default)).WorkflowId;Assert.NotNull(await s.BeginNextDispatchAsync(default));}
        await using(SchedulingDbContext restarted=fixture.CreateScheduling()){var s=new SchedulingService(restarted,fake,accessor,new FixedClock(Now));Assert.Null(await s.BeginNextDispatchAsync(default));Assert.Equal("ReconciliationPending",(await s.GetStatusAsync(ids.Location,workflow,default)).State);Assert.Equal(0,fake.BookingInvocations);Assert.Single(await restarted.ProviderOperations.Where(x=>x.WorkflowId==workflow&&x.Kind==ProviderOperationKind.ReconcileBooking).ToListAsync());}
    }
    [Fact] public async Task CrossLocationStatusAndOfferAreSafelyHidden()
    {
        await ResetAsync();
        var ids=Scope();var fake=new DeterministicPracticeManagementSystem(new FixedClock(Now));Guid workflow;string token;
        await using(SchedulingDbContext db=fixture.CreateScheduling()){var s=new SchedulingService(db,fake,Accessor(ids),new FixedClock(Now));token=(await s.SearchAvailabilityAsync(Search(ids.Location),default))[0].OfferToken;workflow=(await s.BookAppointmentAsync(new(ids.Location,token,"party","exam","idem-6",1),default)).WorkflowId;}
        Guid other=Guid.NewGuid();var wrong=ids with{Location=other};await using SchedulingDbContext hidden=fixture.CreateScheduling();var denied=new SchedulingService(hidden,fake,Accessor(wrong),new FixedClock(Now));Assert.Equal("scheduling_not_found",(await Assert.ThrowsAsync<SchedulingApplicationException>(()=>denied.GetStatusAsync(ids.Location,workflow,default))).Code);Assert.Empty(await hidden.ProviderOperations.Where(x=>x.LocationId==other).ToListAsync());
    }
    [Fact] public async Task AuditEvidenceIsConsumedOnceInAuditOwnedTransaction()
    {
        var ids=Scope();Guid messageId=Guid.NewGuid();var evidence=new SchedulingAuditEvidence(Guid.NewGuid(),"AppointmentBookingRequested","accepted","requested",Now);
        await using var eventing=fixture.CreateEventing();await using var tenancy=fixture.CreateTenancy();var consumer=new SchedulingAuditEvidenceConsumer(new InboxDeduplicationStore(eventing,new FixedClock(Now)),new SecurityAuditService((IAuditWriter)tenancy,new FixedClock(Now)));
        Assert.True(await consumer.ConsumeAsync(messageId,ids.Tenant,ids.Location,"synthetic-actor",ids.Correlation,evidence,default));Assert.False(await consumer.ConsumeAsync(messageId,ids.Tenant,ids.Location,"synthetic-actor",ids.Correlation,evidence,default));Assert.Equal(1,await tenancy.AuditRecords.CountAsync(x=>x.TenantId==ids.Tenant&&x.CorrelationId==ids.Correlation));
    }
    private static SearchAvailabilityRequest Search(Guid location)=>new(location,Now.AddDays(1),Now.AddDays(5),"America/Puerto_Rico","exam");
    private async Task ResetAsync(){await using SchedulingDbContext db=fixture.CreateScheduling();await db.AppointmentProjections.ExecuteDeleteAsync();await db.ProviderOperations.ExecuteDeleteAsync();await db.IdempotencyReceipts.ExecuteDeleteAsync();await db.AppointmentWorkflows.ExecuteDeleteAsync();await db.AvailabilityOffers.ExecuteDeleteAsync();}
    private static (Guid Tenant,Guid Location,Guid Correlation) Scope()=>(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
    private static TestAccessor Accessor((Guid Tenant,Guid Location,Guid Correlation) s)=>new(new(s.Tenant,s.Location,"tester","administrator",s.Correlation,AuthorizedLocationIds:new HashSet<Guid>{s.Location},Permissions:new HashSet<string>{"scheduling.read","scheduling.write"}));
    private sealed class TestAccessor(RequestContext current):IRequestContextAccessor{public RequestContext Current{get;}=current;}
    private sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
}
