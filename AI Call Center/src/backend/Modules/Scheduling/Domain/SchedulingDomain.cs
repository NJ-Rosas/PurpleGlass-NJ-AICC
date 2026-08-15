namespace PurpleGlass.Modules.Scheduling.Domain;

public enum AvailabilityOfferState { Available = 1, Consumed = 2, Invalidated = 3 }
public enum AppointmentWorkflowState
{
    Requested = 1, ProviderOperationPending = 2, Confirmed = 3, Rejected = 4,
    Conflict = 5, RetryPending = 6, OutcomeUnknown = 7, ReconciliationPending = 8,
    Reconciled = 9, FailedPermanently = 10, OutcomeUnresolved = 11, Superseded = 12,
}
public enum ProviderOperationKind { Book = 1, ReconcileBooking = 2 }
public enum ProviderOperationState { Pending = 1, Dispatching = 2, Completed = 3, Failed = 4 }

public sealed class AvailabilityOffer
{
    private AvailabilityOffer() { }
    public AvailabilityOffer(Guid id, Guid tenantId, Guid locationId, string appointmentTypeCode,
        string tokenHash, DateTimeOffset startUtc, DateTimeOffset endUtc, string officeTimeZone,
        string sourceVersion, string providerSlotReference, string? resourceReference, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || locationId == Guid.Empty) throw new ArgumentException("Identifiers are required.");
        if (string.IsNullOrWhiteSpace(appointmentTypeCode) || appointmentTypeCode.Length > 80) throw new ArgumentException("A bounded appointment type is required.");
        if (tokenHash.Length != 64) throw new ArgumentException("A SHA-256 token hash is required.");
        if (startUtc >= endUtc || createdAtUtc >= expiresAtUtc) throw new ArgumentException("Offer times are invalid.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(officeTimeZone); } catch (TimeZoneNotFoundException) { throw new ArgumentException("The office time zone is invalid."); }
        Id = id; TenantId = tenantId; LocationId = locationId; AppointmentTypeCode = appointmentTypeCode.Trim();
        TokenHash = tokenHash; StartUtc = startUtc; EndUtc = endUtc; OfficeTimeZone = officeTimeZone;
        SourceVersion = Require(sourceVersion, 120); ProviderSlotReference = Require(providerSlotReference, 200); ResourceReference = Optional(resourceReference, 200);
        CreatedAtUtc = createdAtUtc; ExpiresAtUtc = expiresAtUtc; State = AvailabilityOfferState.Available; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LocationId { get; private set; }
    public string AppointmentTypeCode { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset StartUtc { get; private set; }
    public DateTimeOffset EndUtc { get; private set; }
    public string OfficeTimeZone { get; private set; } = string.Empty;
    public string SourceVersion { get; private set; } = string.Empty;
    public string ProviderSlotReference { get; private set; } = string.Empty;
    public string? ResourceReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public AvailabilityOfferState State { get; private set; }
    public Guid? ConsumedByWorkflowId { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
    public long Version { get; private set; }
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
    public void Consume(Guid workflowId, DateTimeOffset now)
    {
        if (State != AvailabilityOfferState.Available) throw new InvalidOperationException("availability_offer_unavailable");
        if (IsExpired(now)) throw new InvalidOperationException("availability_offer_expired");
        if (workflowId == Guid.Empty) throw new ArgumentException("Workflow identifier is required.");
        State = AvailabilityOfferState.Consumed; ConsumedByWorkflowId = workflowId; ConsumedAtUtc = now; Version++;
    }
    public void Invalidate() { if (State != AvailabilityOfferState.Available) throw new InvalidOperationException("availability_offer_unavailable"); State = AvailabilityOfferState.Invalidated; Version++; }
    private static string Require(string value, int max) { string n = value.Trim(); if (n.Length is 0 || n.Length > max) throw new ArgumentException("A bounded value is required."); return n; }
    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Require(value, max);
}

public sealed class AppointmentWorkflow
{
    private AppointmentWorkflow() { }
    public AppointmentWorkflow(Guid id, Guid tenantId, Guid locationId, Guid offerId, string appointmentTypeCode,
        string partyReference, string semanticFingerprint, Guid correlationId, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || locationId == Guid.Empty || offerId == Guid.Empty) throw new ArgumentException("Identifiers are required.");
        Id=id; TenantId=tenantId; LocationId=locationId; OfferId=offerId; AppointmentTypeCode=Bound(appointmentTypeCode,80);
        PartyReference=Bound(partyReference,200); SemanticFingerprint=Bound(semanticFingerprint,64); CorrelationId=correlationId;
        State=AppointmentWorkflowState.Requested; ResultCode="requested"; CreatedAtUtc=LastTransitionAtUtc=now; Version=1;
    }
    public Guid Id { get; private set; } public Guid TenantId { get; private set; } public Guid LocationId { get; private set; }
    public Guid OfferId { get; private set; } public string AppointmentTypeCode { get; private set; }=string.Empty;
    public string PartyReference { get; private set; }=string.Empty; public string SemanticFingerprint { get; private set; }=string.Empty;
    public Guid CorrelationId { get; private set; } public AppointmentWorkflowState State { get; private set; }
    public string ResultCode { get; private set; }=string.Empty; public bool ManualReviewRequired { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; } public DateTimeOffset LastTransitionAtUtc { get; private set; }
    public long Version { get; private set; }
    public void QueueProvider(DateTimeOffset now) => Move(AppointmentWorkflowState.ProviderOperationPending,"provider_operation_pending",now, AppointmentWorkflowState.Requested, AppointmentWorkflowState.RetryPending);
    public void Confirm(DateTimeOffset now, bool reconciled=false) { if(reconciled) Move(AppointmentWorkflowState.Reconciled,"reconciled",now,AppointmentWorkflowState.ReconciliationPending); Move(AppointmentWorkflowState.Confirmed,"confirmed",now,reconciled?AppointmentWorkflowState.Reconciled:AppointmentWorkflowState.ProviderOperationPending); }
    public void Conflict(string code, DateTimeOffset now) => Move(AppointmentWorkflowState.Conflict,Bound(code,100),now,AppointmentWorkflowState.ProviderOperationPending);
    public void Reject(string code, DateTimeOffset now) => Move(AppointmentWorkflowState.Rejected,Bound(code,100),now,AppointmentWorkflowState.Requested,AppointmentWorkflowState.ProviderOperationPending);
    public void Retry(DateTimeOffset now) => Move(AppointmentWorkflowState.RetryPending,"retry_pending",now,AppointmentWorkflowState.ProviderOperationPending);
    public void MarkUnknown(DateTimeOffset now) { Move(AppointmentWorkflowState.OutcomeUnknown,"outcome_unknown",now,AppointmentWorkflowState.ProviderOperationPending); Move(AppointmentWorkflowState.ReconciliationPending,"reconciliation_pending",now,AppointmentWorkflowState.OutcomeUnknown); }
    public void FailAuthoritatively(string code, DateTimeOffset now) { Move(AppointmentWorkflowState.Reconciled,"reconciled",now,AppointmentWorkflowState.ReconciliationPending); Move(AppointmentWorkflowState.FailedPermanently,Bound(code,100),now,AppointmentWorkflowState.Reconciled); }
    public void FailBeforeAcceptance(string code,DateTimeOffset now)=>Move(AppointmentWorkflowState.FailedPermanently,Bound(code,100),now,AppointmentWorkflowState.ProviderOperationPending,AppointmentWorkflowState.RetryPending);
    public void MarkUnresolved(DateTimeOffset now) { Move(AppointmentWorkflowState.OutcomeUnresolved,"outcome_unresolved",now,AppointmentWorkflowState.ReconciliationPending); ManualReviewRequired=true; }
    private void Move(AppointmentWorkflowState target,string code,DateTimeOffset now,params AppointmentWorkflowState[] allowed) { if(!allowed.Contains(State)) throw new InvalidOperationException("appointment_workflow_invalid_transition"); State=target;ResultCode=code;LastTransitionAtUtc=now;Version++; }
    private static string Bound(string value,int max) { string n=value.Trim();if(n.Length is 0||n.Length>max)throw new ArgumentException("A bounded value is required.");return n; }
}

public sealed class ProviderOperation
{
    private ProviderOperation() { }
    public ProviderOperation(Guid id, Guid tenantId, Guid locationId, Guid workflowId, ProviderOperationKind kind, int attempt, DateTimeOffset now)
    { if(id==Guid.Empty)throw new ArgumentException("Identifier is required.");Id=id;TenantId=tenantId;LocationId=locationId;WorkflowId=workflowId;Kind=kind;Attempt=attempt;State=ProviderOperationState.Pending;CreatedAtUtc=NextAttemptAtUtc=now;Version=1; }
    public Guid Id{get;private set;} public Guid TenantId{get;private set;} public Guid LocationId{get;private set;} public Guid WorkflowId{get;private set;}
    public ProviderOperationKind Kind{get;private set;} public ProviderOperationState State{get;private set;} public int Attempt{get;private set;}
    public DateTimeOffset CreatedAtUtc{get;private set;} public DateTimeOffset NextAttemptAtUtc{get;private set;} public DateTimeOffset? DispatchStartedAtUtc{get;private set;}
    public DateTimeOffset? CompletedAtUtc{get;private set;} public string? ResultCode{get;private set;} public long Version{get;private set;}
    public void Begin(DateTimeOffset now){if(State!=ProviderOperationState.Pending)throw new InvalidOperationException("provider_operation_invalid_state");State=ProviderOperationState.Dispatching;DispatchStartedAtUtc=now;Version++;}
    public void Complete(string code,DateTimeOffset now){if(State!=ProviderOperationState.Dispatching)throw new InvalidOperationException("provider_operation_invalid_state");State=ProviderOperationState.Completed;ResultCode=code;CompletedAtUtc=now;Version++;}
    public void ScheduleRetry(DateTimeOffset next){if(State!=ProviderOperationState.Dispatching)throw new InvalidOperationException("provider_operation_invalid_state");State=ProviderOperationState.Pending;Attempt++;NextAttemptAtUtc=next;Version++;}
}

public sealed class AppointmentProjection
{
    private AppointmentProjection() { }
    public AppointmentProjection(Guid id,Guid tenantId,Guid locationId,Guid workflowId,string protectedExternalReference,DateTimeOffset startUtc,DateTimeOffset endUtc,string officeTimeZone,string sourceVersion,DateTimeOffset authoritativeAtUtc)
    {Id=id;TenantId=tenantId;LocationId=locationId;WorkflowId=workflowId;ProtectedExternalReference=protectedExternalReference;StartUtc=startUtc;EndUtc=endUtc;OfficeTimeZone=officeTimeZone;SourceVersion=sourceVersion;AuthoritativeAtUtc=authoritativeAtUtc;Version=1;}
    public Guid Id{get;private set;}public Guid TenantId{get;private set;}public Guid LocationId{get;private set;}public Guid WorkflowId{get;private set;}
    public string ProtectedExternalReference{get;private set;}=string.Empty;public DateTimeOffset StartUtc{get;private set;}public DateTimeOffset EndUtc{get;private set;}
    public string OfficeTimeZone{get;private set;}=string.Empty;public string SourceVersion{get;private set;}=string.Empty;public DateTimeOffset AuthoritativeAtUtc{get;private set;}public long Version{get;private set;}
}

public sealed class IdempotencyReceipt
{
    private IdempotencyReceipt() { }
    public IdempotencyReceipt(Guid id,Guid tenantId,Guid locationId,string operation,string keyHash,string fingerprint,Guid workflowId,DateTimeOffset now)
    {Id=id;TenantId=tenantId;LocationId=locationId;Operation=operation;KeyHash=keyHash;SemanticFingerprint=fingerprint;WorkflowId=workflowId;CreatedAtUtc=now;}
    public Guid Id{get;private set;}public Guid TenantId{get;private set;}public Guid LocationId{get;private set;}public string Operation{get;private set;}=string.Empty;
    public string KeyHash{get;private set;}=string.Empty;public string SemanticFingerprint{get;private set;}=string.Empty;public Guid WorkflowId{get;private set;}public DateTimeOffset CreatedAtUtc{get;private set;}
}
