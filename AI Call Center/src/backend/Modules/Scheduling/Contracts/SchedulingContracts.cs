namespace PurpleGlass.Modules.Scheduling.Contracts;
public sealed class SchedulingContractsAssembly;
public sealed record AvailabilityOfferView(string OfferToken,DateTimeOffset StartUtc,DateTimeOffset EndUtc,string OfficeTimeZone,string SourceVersion,DateTimeOffset ExpiresAtUtc,long Version,string? ResourceReference);
public sealed record AppointmentProjectionView(DateTimeOffset StartUtc,DateTimeOffset EndUtc,string OfficeTimeZone,string SourceVersion,string? ProtectedExternalReference);
public sealed record AppointmentWorkflowStatus(Guid WorkflowId,string State,long Version,string ResultCode,bool WorkPending,bool ReconciliationPending,bool ManualReviewRequired,DateTimeOffset LastTransitionAtUtc,AppointmentProjectionView? Appointment);
public sealed record SchedulingAuditEvidence(Guid WorkflowId,string Action,string Outcome,string ReasonCode,DateTimeOffset OccurredAtUtc);
