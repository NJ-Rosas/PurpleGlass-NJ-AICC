using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Scheduling.Contracts;
using PurpleGlass.Modules.Scheduling.Domain;

namespace PurpleGlass.Modules.Scheduling.Application;

public sealed class SchedulingApplicationException(string code,string message):Exception(message){public string Code{get;}=code;public static SchedulingApplicationException NotFound()=>new("scheduling_not_found","The requested scheduling resource was not found.");}
public sealed record SearchAvailabilityRequest(Guid LocationId,DateTimeOffset WindowStartUtc,DateTimeOffset WindowEndUtc,string OfficeTimeZone,string AppointmentTypeCode,string? ResourceCriteria=null);
public sealed record BookAppointmentRequest(Guid LocationId,string OfferToken,string PartyReference,string AppointmentTypeCode,string IdempotencyKey,long ExpectedOfferVersion,Guid? CausationId=null);
public sealed record ProviderAvailabilityRequest(Guid TenantId,Guid LocationId,DateTimeOffset WindowStartUtc,DateTimeOffset WindowEndUtc,string OfficeTimeZone,string AppointmentTypeCode,string? ResourceCriteria);
public sealed record ProviderAvailabilitySlot(string SlotReference,DateTimeOffset StartUtc,DateTimeOffset EndUtc,string SourceVersion,string? ResourceReference);
public enum ProviderBookingCategory{Confirmed,Conflict,Rejected,RetryablePreAcceptance,OutcomeUnknown}
public sealed record ProviderBookingRequest(Guid OperationId,Guid WorkflowId,Guid TenantId,Guid LocationId,string SlotReference,string PartyReference,string AppointmentTypeCode,DateTimeOffset StartUtc,DateTimeOffset EndUtc,string OfficeTimeZone,string SourceVersion);
public sealed record ProviderBookingResult(ProviderBookingCategory Category,string ResultCode,string? ProtectedExternalReference=null,string? SourceVersion=null,DateTimeOffset? AuthoritativeAtUtc=null);
public enum ProviderReconciliationCategory{Confirmed,NotCreated,StillUnknown}
public sealed record ProviderReconciliationRequest(Guid OperationId,Guid WorkflowId,Guid TenantId,Guid LocationId);
public sealed record ProviderReconciliationResult(ProviderReconciliationCategory Category,string ResultCode,string? ProtectedExternalReference=null,string? SourceVersion=null,DateTimeOffset? AuthoritativeAtUtc=null);
public interface IPracticeManagementSystem
{
    string Name{get;}
    Task<IReadOnlyList<ProviderAvailabilitySlot>> SearchAvailabilityAsync(ProviderAvailabilityRequest request,CancellationToken cancellationToken);
    Task<ProviderBookingResult> BookAppointmentAsync(ProviderBookingRequest request,CancellationToken cancellationToken);
    Task<ProviderReconciliationResult> ReconcileBookingAsync(ProviderReconciliationRequest request,CancellationToken cancellationToken);
}
public sealed record SchedulingDispatch(Guid OperationId,ProviderOperationKind Kind,int Attempt,Guid WorkflowId,Guid TenantId,Guid LocationId,string SlotReference,string PartyReference,string AppointmentTypeCode,DateTimeOffset StartUtc,DateTimeOffset EndUtc,string OfficeTimeZone,string SourceVersion);
public interface ISchedulingStore
{
    Task AddOffersAsync(IEnumerable<AvailabilityOffer> offers,CancellationToken cancellationToken);
    Task<AvailabilityOffer?> GetOfferByHashAsync(Guid tenantId,Guid locationId,string tokenHash,bool tracking,CancellationToken cancellationToken);
    Task<AvailabilityOffer?> GetOfferByIdAsync(Guid tenantId,Guid locationId,Guid offerId,CancellationToken cancellationToken);
    Task<IdempotencyReceipt?> GetReceiptAsync(Guid tenantId,Guid locationId,string operation,string keyHash,CancellationToken cancellationToken);
    Task<AppointmentWorkflow?> GetWorkflowAsync(Guid tenantId,Guid locationId,Guid workflowId,bool tracking,CancellationToken cancellationToken);
    Task<AppointmentProjection?> GetProjectionAsync(Guid tenantId,Guid locationId,Guid workflowId,CancellationToken cancellationToken);
    Task<ProviderOperation?> GetNextOperationAsync(DateTimeOffset now,CancellationToken cancellationToken);
    Task<ProviderOperation?> GetOperationAsync(Guid operationId,bool tracking,CancellationToken cancellationToken);
    void Add(AppointmentWorkflow workflow);void Add(ProviderOperation operation);void Add(IdempotencyReceipt receipt);void Add(AppointmentProjection projection);void AddOutbox(OutboxMessage message);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class SchedulingService(ISchedulingStore store,IPracticeManagementSystem provider,IRequestContextAccessor contextAccessor,TimeProvider timeProvider)
{
    private RequestContext Context=>contextAccessor.Current;
    public async Task<IReadOnlyList<AvailabilityOfferView>> SearchAvailabilityAsync(SearchAvailabilityRequest request,CancellationToken cancellationToken)
    {
        Authorize(request.LocationId,"scheduling.read"); DateTimeOffset now=timeProvider.GetUtcNow();
        if(request.WindowStartUtc<now.AddMinutes(-1)||request.WindowEndUtc<=request.WindowStartUtc||request.WindowEndUtc-request.WindowStartUtc>TimeSpan.FromDays(31))throw new SchedulingApplicationException("invalid_availability_window","The availability window is invalid.");
        try{_=TimeZoneInfo.FindSystemTimeZoneById(request.OfficeTimeZone);}catch(TimeZoneNotFoundException){throw new SchedulingApplicationException("invalid_time_zone","The office time zone is invalid.");}
        IReadOnlyList<ProviderAvailabilitySlot> slots=await provider.SearchAvailabilityAsync(new(Context.TenantId,request.LocationId,request.WindowStartUtc,request.WindowEndUtc,request.OfficeTimeZone,Bound(request.AppointmentTypeCode,80),request.ResourceCriteria),cancellationToken);
        var output=new List<AvailabilityOfferView>();var offers=new List<AvailabilityOffer>();
        foreach(ProviderAvailabilitySlot slot in slots.Take(100))
        {
            string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));string hash=Hash(token);DateTimeOffset expires=now.AddMinutes(10);
            var offer=new AvailabilityOffer(Guid.NewGuid(),Context.TenantId,request.LocationId,request.AppointmentTypeCode,hash,slot.StartUtc,slot.EndUtc,request.OfficeTimeZone,slot.SourceVersion,slot.SlotReference,slot.ResourceReference,now,expires);
            offers.Add(offer);output.Add(new(token,slot.StartUtc,slot.EndUtc,request.OfficeTimeZone,slot.SourceVersion,expires,offer.Version,slot.ResourceReference));
        }
        await store.AddOffersAsync(offers,cancellationToken);return output;
    }
    public async Task<AppointmentWorkflowStatus> BookAppointmentAsync(BookAppointmentRequest request,CancellationToken cancellationToken)
    {
        Authorize(request.LocationId,"scheduling.write"); string keyHash=Hash(Bound(request.IdempotencyKey,200));string tokenHash=Hash(Bound(request.OfferToken,256));
        string fingerprint=Hash($"{request.LocationId:D}|{tokenHash}|{request.AppointmentTypeCode.Trim().ToUpperInvariant()}|{request.PartyReference.Trim()}");
        IdempotencyReceipt? receipt=await store.GetReceiptAsync(Context.TenantId,request.LocationId,"book",keyHash,cancellationToken);
        if(receipt is not null){if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(receipt.SemanticFingerprint),Convert.FromHexString(fingerprint)))throw new SchedulingApplicationException("idempotency_conflict","The idempotency key was already used for different booking semantics.");return await GetStatusAsync(request.LocationId,receipt.WorkflowId,cancellationToken);}
        AvailabilityOffer? offer=await store.GetOfferByHashAsync(Context.TenantId,request.LocationId,tokenHash,true,cancellationToken)??throw SchedulingApplicationException.NotFound();
        DateTimeOffset now=timeProvider.GetUtcNow();
        if(offer.Version!=request.ExpectedOfferVersion)throw new SchedulingApplicationException("concurrency_conflict","The availability offer changed.");
        if(offer.IsExpired(now))throw new SchedulingApplicationException("availability_expired","The availability offer expired.");
        if(offer.State!=AvailabilityOfferState.Available)throw new SchedulingApplicationException("availability_conflict","The availability offer is no longer available.");
        if(!string.Equals(offer.AppointmentTypeCode,request.AppointmentTypeCode.Trim(),StringComparison.OrdinalIgnoreCase))throw new SchedulingApplicationException("appointment_type_mismatch","The appointment type does not match the offer.");
        Guid workflowId=Guid.NewGuid();var workflow=new AppointmentWorkflow(workflowId,Context.TenantId,request.LocationId,offer.Id,offer.AppointmentTypeCode,Bound(request.PartyReference,200),fingerprint,Context.CorrelationId,now);
        offer.Consume(workflowId,now);workflow.QueueProvider(now);var operation=new ProviderOperation(Guid.NewGuid(),Context.TenantId,request.LocationId,workflowId,ProviderOperationKind.Book,1,now);
        store.Add(workflow);store.Add(operation);store.Add(new IdempotencyReceipt(Guid.NewGuid(),Context.TenantId,request.LocationId,"book",keyHash,fingerprint,workflowId,now));
        AddFact(workflow,"AppointmentBookingRequested","requested",now,request.CausationId);AddFact(workflow,"SchedulingAuditEvidence","accepted",now,request.CausationId);
        try{await store.SaveChangesAsync(cancellationToken);}catch(Exception ex)when(ex.GetType().Name.Contains("Concurrency",StringComparison.Ordinal)){throw new SchedulingApplicationException("concurrency_conflict","The availability offer changed.");}
        return Map(workflow,null);
    }
    public async Task<AppointmentWorkflowStatus> GetStatusAsync(Guid locationId,Guid workflowId,CancellationToken cancellationToken)
    {
        Authorize(locationId,"scheduling.read");AppointmentWorkflow workflow=await store.GetWorkflowAsync(Context.TenantId,locationId,workflowId,false,cancellationToken)??throw SchedulingApplicationException.NotFound();
        return Map(workflow,await store.GetProjectionAsync(Context.TenantId,locationId,workflowId,cancellationToken));
    }
    public async Task<SchedulingDispatch?> BeginNextDispatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now=timeProvider.GetUtcNow();ProviderOperation? operation=await store.GetNextOperationAsync(now,cancellationToken);if(operation is null)return null;
        AppointmentWorkflow workflow=await store.GetWorkflowAsync(operation.TenantId,operation.LocationId,operation.WorkflowId,true,cancellationToken)??throw SchedulingApplicationException.NotFound();
        AvailabilityOffer offer=await store.GetOfferByIdAsync(operation.TenantId,operation.LocationId,workflow.OfferId,cancellationToken)??throw SchedulingApplicationException.NotFound();
        if(operation.State==ProviderOperationState.Dispatching&&operation.Kind==ProviderOperationKind.Book)
        {
            if(workflow.State==AppointmentWorkflowState.ProviderOperationPending)workflow.MarkUnknown(now);
            operation.Complete("dispatch_result_lost",now);store.Add(new ProviderOperation(Guid.NewGuid(),operation.TenantId,operation.LocationId,operation.WorkflowId,ProviderOperationKind.ReconcileBooking,1,now));AddFact(workflow,"AppointmentReconciliationRequested","restart_recovery",now,null);await store.SaveChangesAsync(cancellationToken);return null;
        }
        operation.Begin(now);await store.SaveChangesAsync(cancellationToken);
        return new(operation.Id,operation.Kind,operation.Attempt,workflow.Id,workflow.TenantId,workflow.LocationId,offer.ProviderSlotReference,workflow.PartyReference,workflow.AppointmentTypeCode,offer.StartUtc,offer.EndUtc,offer.OfficeTimeZone,offer.SourceVersion);
    }
    public async Task ApplyBookingResultAsync(Guid operationId,ProviderBookingResult result,CancellationToken cancellationToken)
    {
        ProviderOperation operation=await store.GetOperationAsync(operationId,true,cancellationToken)??throw SchedulingApplicationException.NotFound();if(operation.State==ProviderOperationState.Completed)return;
        AppointmentWorkflow workflow=await store.GetWorkflowAsync(operation.TenantId,operation.LocationId,operation.WorkflowId,true,cancellationToken)??throw SchedulingApplicationException.NotFound();DateTimeOffset now=timeProvider.GetUtcNow();
        switch(result.Category)
        {
            case ProviderBookingCategory.Confirmed: workflow.Confirm(now);await AddProjection(workflow,result,now,cancellationToken);operation.Complete("confirmed",now);AddFact(workflow,"AppointmentConfirmed","confirmed",now,null);break;
            case ProviderBookingCategory.Conflict:workflow.Conflict("provider_conflict",now);operation.Complete("provider_conflict",now);AddFact(workflow,"AppointmentConflict","provider_conflict",now,null);break;
            case ProviderBookingCategory.Rejected:workflow.Reject("provider_rejected",now);operation.Complete("provider_rejected",now);AddFact(workflow,"AppointmentRejected","provider_rejected",now,null);break;
            case ProviderBookingCategory.RetryablePreAcceptance when operation.Attempt<3:workflow.Retry(now);operation.ScheduleRetry(now.AddSeconds(Math.Pow(2,operation.Attempt)));workflow.QueueProvider(now);AddFact(workflow,"AppointmentRetryScheduled","safe_pre_acceptance",now,null);break;
            case ProviderBookingCategory.RetryablePreAcceptance:workflow.FailBeforeAcceptance("provider_retry_exhausted",now);operation.Complete("provider_retry_exhausted",now);AddFact(workflow,"AppointmentFailedPermanently","provider_retry_exhausted",now,null);break;
            default:workflow.MarkUnknown(now);operation.Complete("outcome_unknown",now);store.Add(new ProviderOperation(Guid.NewGuid(),workflow.TenantId,workflow.LocationId,workflow.Id,ProviderOperationKind.ReconcileBooking,1,now));AddFact(workflow,"AppointmentReconciliationRequested","outcome_unknown",now,null);break;
        }
        await store.SaveChangesAsync(cancellationToken);
    }
    public async Task ApplyReconciliationResultAsync(Guid operationId,ProviderReconciliationResult result,CancellationToken cancellationToken)
    {
        ProviderOperation operation=await store.GetOperationAsync(operationId,true,cancellationToken)??throw SchedulingApplicationException.NotFound();if(operation.State==ProviderOperationState.Completed)return;
        AppointmentWorkflow workflow=await store.GetWorkflowAsync(operation.TenantId,operation.LocationId,operation.WorkflowId,true,cancellationToken)??throw SchedulingApplicationException.NotFound();DateTimeOffset now=timeProvider.GetUtcNow();
        if(workflow.State!=AppointmentWorkflowState.ReconciliationPending){operation.Complete("stale_result",now);await store.SaveChangesAsync(cancellationToken);return;}
        if(result.Category==ProviderReconciliationCategory.Confirmed){workflow.Confirm(now,true);await AddProjection(workflow,new(ProviderBookingCategory.Confirmed,"confirmed",result.ProtectedExternalReference,result.SourceVersion,result.AuthoritativeAtUtc),now,cancellationToken);operation.Complete("confirmed",now);AddFact(workflow,"AppointmentReconciled","confirmed",now,null);}
        else if(result.Category==ProviderReconciliationCategory.NotCreated){workflow.FailAuthoritatively("provider_not_created",now);operation.Complete("not_created",now);AddFact(workflow,"AppointmentFailedPermanently","provider_not_created",now,null);}
        else if(operation.Attempt<3){operation.ScheduleRetry(now.AddSeconds(Math.Pow(2,operation.Attempt)));AddFact(workflow,"AppointmentReconciliationRetryScheduled","still_unknown",now,null);}
        else{workflow.MarkUnresolved(now);operation.Complete("outcome_unresolved",now);AddFact(workflow,"AppointmentOutcomeUnresolved","manual_review_required",now,null);}
        await store.SaveChangesAsync(cancellationToken);
    }
    private async Task AddProjection(AppointmentWorkflow workflow,ProviderBookingResult result,DateTimeOffset now,CancellationToken ct)
    {string external=result.ProtectedExternalReference??throw new SchedulingApplicationException("provider_invalid_response","The provider result was incomplete.");AvailabilityOffer offer=await store.GetOfferByIdAsync(workflow.TenantId,workflow.LocationId,workflow.OfferId,ct)??throw SchedulingApplicationException.NotFound();store.Add(new AppointmentProjection(Guid.NewGuid(),workflow.TenantId,workflow.LocationId,workflow.Id,external,offer.StartUtc,offer.EndUtc,offer.OfficeTimeZone,result.SourceVersion??offer.SourceVersion,result.AuthoritativeAtUtc??now));}
    private void AddFact(AppointmentWorkflow w,string type,string reason,DateTimeOffset now,Guid? causation)
    {var payload=JsonSerializer.Serialize(new SchedulingAuditEvidence(w.Id,type,w.State.ToString(),reason,now));store.AddOutbox(OutboxMessage.Create(w.TenantId,w.LocationId,$"purpleglass/v1/tenants/{w.TenantId:D}/locations/{w.LocationId:D}/scheduling",type,payload,w.CorrelationId,now,causationId:causation,producer:"purpleglass-scheduling",dataClassification:"internal"));}
    private void Authorize(Guid location,string permission){if(Context.TenantId==Guid.Empty||Context.LocationId!=location||!Context.CanAccessLocation(location)||!Context.HasPermission(permission))throw SchedulingApplicationException.NotFound();}
    public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Bound(string value,int max){string n=value.Trim();if(n.Length is 0||n.Length>max)throw new SchedulingApplicationException("invalid_request","A required bounded value was invalid.");return n;}
    private static AppointmentWorkflowStatus Map(AppointmentWorkflow w,AppointmentProjection? p)=>new(w.Id,w.State.ToString(),w.Version,w.ResultCode,w.State is AppointmentWorkflowState.ProviderOperationPending or AppointmentWorkflowState.RetryPending,w.State==AppointmentWorkflowState.ReconciliationPending,w.ManualReviewRequired,w.LastTransitionAtUtc,p is null?null:new(p.StartUtc,p.EndUtc,p.OfficeTimeZone,p.SourceVersion,p.ProtectedExternalReference));
}
