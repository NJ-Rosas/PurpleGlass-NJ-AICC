using PurpleGlass.Adapters.PracticeManagement.Deterministic;
using PurpleGlass.Modules.Scheduling.Application;
using PurpleGlass.Modules.Scheduling.Domain;
namespace PurpleGlass.UnitTests;
public sealed class SchedulingTests
{
    private static readonly DateTimeOffset Now=new(2026,8,5,12,0,0,TimeSpan.Zero);
    [Fact] public void OfferExpiresAndConsumesOnce()
    {
        var offer=Offer();Assert.False(offer.IsExpired(Now));offer.Consume(Guid.NewGuid(),Now);Assert.Equal(AvailabilityOfferState.Consumed,offer.State);Assert.Throws<InvalidOperationException>(()=>offer.Consume(Guid.NewGuid(),Now));
    }
    [Fact] public void ExpiredOfferCannotBeConsumed(){var offer=Offer();Assert.Throws<InvalidOperationException>(()=>offer.Consume(Guid.NewGuid(),Now.AddMinutes(10)));}
    [Fact] public void WorkflowHappyPathIsExplicitAndVersioned()
    {var w=Workflow();long v=w.Version;w.QueueProvider(Now);w.Confirm(Now);Assert.Equal(AppointmentWorkflowState.Confirmed,w.State);Assert.True(w.Version>v);Assert.Throws<InvalidOperationException>(()=>w.MarkUnknown(Now));}
    [Fact] public void UnknownOutcomeRequiresReconciliationAndCanBecomeUnresolved()
    {var w=Workflow();w.QueueProvider(Now);w.MarkUnknown(Now);Assert.Equal(AppointmentWorkflowState.ReconciliationPending,w.State);w.MarkUnresolved(Now);Assert.True(w.ManualReviewRequired);Assert.Equal(AppointmentWorkflowState.OutcomeUnresolved,w.State);}
    [Fact] public void ReconciliationCanConfirmOrProveFailure()
    {var confirmed=Workflow();confirmed.QueueProvider(Now);confirmed.MarkUnknown(Now);confirmed.Confirm(Now,true);Assert.Equal(AppointmentWorkflowState.Confirmed,confirmed.State);var failed=Workflow();failed.QueueProvider(Now);failed.MarkUnknown(Now);failed.FailAuthoritatively("not_created",Now);Assert.Equal(AppointmentWorkflowState.FailedPermanently,failed.State);}
    [Fact] public async Task SimulatorIsDeterministicAndIdempotent()
    {
        var clock=new FixedTimeProvider(Now);var fake=new DeterministicPracticeManagementSystem(clock);Guid workflow=Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");var request=new ProviderBookingRequest(Guid.NewGuid(),workflow,Guid.NewGuid(),Guid.NewGuid(),"slot","party","exam",Now.AddDays(1),Now.AddDays(1).AddMinutes(30),"America/Puerto_Rico","v1");ProviderBookingResult first=await fake.BookAppointmentAsync(request,default);ProviderBookingResult second=await fake.BookAppointmentAsync(request with{OperationId=Guid.NewGuid()},default);Assert.Equal(first,second);Assert.Equal($"sim-{workflow:N}",first.ProtectedExternalReference);
    }
    [Theory][InlineData(DeterministicBookingScenario.Conflict,ProviderBookingCategory.Conflict)][InlineData(DeterministicBookingScenario.RetryablePreAcceptance,ProviderBookingCategory.RetryablePreAcceptance)][InlineData(DeterministicBookingScenario.UnknownAfterAcceptance,ProviderBookingCategory.OutcomeUnknown)]
    public async Task SimulatorNormalizesScenarios(DeterministicBookingScenario scenario,ProviderBookingCategory category)
    {var fake=new DeterministicPracticeManagementSystem(new FixedTimeProvider(Now)){BookingScenario=scenario};var r=await fake.BookAppointmentAsync(new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"slot","party","exam",Now,Now.AddMinutes(30),"America/Puerto_Rico","v1"),default);Assert.Equal(category,r.Category);}
    [Fact] public async Task AcceptedUnknownIsFoundByReconciliation()
    {var fake=new DeterministicPracticeManagementSystem(new FixedTimeProvider(Now)){BookingScenario=DeterministicBookingScenario.UnknownAfterAcceptance};Guid workflow=Guid.NewGuid();await fake.BookAppointmentAsync(new(Guid.NewGuid(),workflow,Guid.NewGuid(),Guid.NewGuid(),"slot","party","exam",Now,Now.AddMinutes(30),"America/Puerto_Rico","v1"),default);var result=await fake.ReconcileBookingAsync(new(Guid.NewGuid(),workflow,Guid.NewGuid(),Guid.NewGuid()),default);Assert.Equal(ProviderReconciliationCategory.Confirmed,result.Category);}
    [Fact] public void SemanticHashIsStableAndCaseSensitiveForOpaqueParty(){Assert.Equal(SchedulingService.Hash("same"),SchedulingService.Hash("same"));Assert.NotEqual(SchedulingService.Hash("same"),SchedulingService.Hash("Same"));}
    private static AvailabilityOffer Offer(DateTimeOffset? expires=null)=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"exam",new string('A',64),Now.AddDays(1),Now.AddDays(1).AddMinutes(30),"America/Puerto_Rico","v1","slot-1","resource-1",Now,expires??Now.AddMinutes(10));
    private static AppointmentWorkflow Workflow()=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"exam","synthetic-party",new string('B',64),Guid.NewGuid(),Now);
    private sealed class FixedTimeProvider(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
}
