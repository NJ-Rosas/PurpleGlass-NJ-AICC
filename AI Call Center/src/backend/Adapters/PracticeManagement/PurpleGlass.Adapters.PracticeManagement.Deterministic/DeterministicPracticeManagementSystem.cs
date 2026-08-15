using System.Collections.Concurrent;
using PurpleGlass.Modules.Scheduling.Application;
namespace PurpleGlass.Adapters.PracticeManagement.Deterministic;
public sealed class DeterministicPracticeManagementAssembly;
public enum DeterministicBookingScenario{Success,Conflict,Rejected,RetryablePreAcceptance,UnknownBeforeAcceptance,UnknownAfterAcceptance}
public enum DeterministicReconciliationScenario{UseRecordedOutcome,NotCreated,StillUnknown}
public sealed class DeterministicPracticeManagementSystem(TimeProvider timeProvider):IPracticeManagementSystem
{
    private readonly ConcurrentDictionary<Guid,ProviderBookingResult> appointments=new();
    public string Name=>"deterministic-fake";
    public DeterministicBookingScenario BookingScenario{get;set;}=DeterministicBookingScenario.Success;
    public DeterministicReconciliationScenario ReconciliationScenario{get;set;}=DeterministicReconciliationScenario.UseRecordedOutcome;
    public int BookingInvocations{get;private set;} public int ReconciliationInvocations{get;private set;}
    public Task<IReadOnlyList<ProviderAvailabilitySlot>> SearchAvailabilityAsync(ProviderAvailabilityRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();DateTimeOffset first=new(request.WindowStartUtc.UtcDateTime.Date.AddHours(14),TimeSpan.Zero);if(first<request.WindowStartUtc)first=first.AddDays(1);
        IReadOnlyList<ProviderAvailabilitySlot> slots=Enumerable.Range(0,3).Select(i=>new ProviderAvailabilitySlot($"slot-{request.LocationId:N}-{i}",first.AddDays(i),first.AddDays(i).AddMinutes(30),$"seed-v1-{i}",$"resource-{i+1}")).Where(x=>x.EndUtc<=request.WindowEndUtc).ToArray();return Task.FromResult(slots);
    }
    public Task<ProviderBookingResult> BookAppointmentAsync(ProviderBookingRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();BookingInvocations++;
        if(appointments.TryGetValue(request.WorkflowId,out ProviderBookingResult? existing))return Task.FromResult(existing);
        string reference=$"sim-{request.WorkflowId:N}";DateTimeOffset now=timeProvider.GetUtcNow();
        ProviderBookingResult result=BookingScenario switch
        {
            DeterministicBookingScenario.Success=>new(ProviderBookingCategory.Confirmed,"confirmed",reference,request.SourceVersion,now),
            DeterministicBookingScenario.Conflict=>new(ProviderBookingCategory.Conflict,"provider_conflict"),
            DeterministicBookingScenario.Rejected=>new(ProviderBookingCategory.Rejected,"provider_rejected"),
            DeterministicBookingScenario.RetryablePreAcceptance=>new(ProviderBookingCategory.RetryablePreAcceptance,"provider_unavailable_pre_acceptance"),
            DeterministicBookingScenario.UnknownBeforeAcceptance=>new(ProviderBookingCategory.OutcomeUnknown,"provider_outcome_unknown"),
            DeterministicBookingScenario.UnknownAfterAcceptance=>new(ProviderBookingCategory.OutcomeUnknown,"provider_outcome_unknown"),
            _=>throw new InvalidOperationException()
        };
        if(BookingScenario is DeterministicBookingScenario.Success or DeterministicBookingScenario.UnknownAfterAcceptance)appointments[request.WorkflowId]=new(ProviderBookingCategory.Confirmed,"confirmed",reference,request.SourceVersion,now);
        return Task.FromResult(result);
    }
    public Task<ProviderReconciliationResult> ReconcileBookingAsync(ProviderReconciliationRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();ReconciliationInvocations++;
        if(ReconciliationScenario==DeterministicReconciliationScenario.StillUnknown)return Task.FromResult(new ProviderReconciliationResult(ProviderReconciliationCategory.StillUnknown,"still_unknown"));
        if(ReconciliationScenario==DeterministicReconciliationScenario.NotCreated)return Task.FromResult(new ProviderReconciliationResult(ProviderReconciliationCategory.NotCreated,"not_created"));
        return Task.FromResult(appointments.TryGetValue(request.WorkflowId,out ProviderBookingResult? found)
            ?new ProviderReconciliationResult(ProviderReconciliationCategory.Confirmed,"confirmed",found.ProtectedExternalReference,found.SourceVersion,found.AuthoritativeAtUtc)
            :new ProviderReconciliationResult(ProviderReconciliationCategory.NotCreated,"not_created"));
    }
}
