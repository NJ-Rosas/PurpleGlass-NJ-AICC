namespace PurpleGlass.Modules.Identity.Domain;

public enum MembershipRole
{
    TenantAdministrator,
    OfficeManager,
    StaffUser,
    ReadOnlyUser
}

public static class SecurityPermissions
{
    public const string ViewLiveCalls = "calls.live.view";
    public const string ViewRecentCalls = "calls.recent.view";
    public const string ViewTranscripts = "transcripts.view";
    public const string ViewSummaries = "summaries.view";
    public const string ViewRecordingMetadata = "recordings.metadata.view";
    public const string InitiateOutboundCalls = "calls.outbound.initiate";
    public const string ManageTenantSettings = "tenant.settings.manage";
    public const string ManageLocationSettings = "location.settings.manage";
    public const string ViewAuditRecords = "audit.view";

    public static IReadOnlySet<string> ForRole(MembershipRole role) => role switch
    {
        MembershipRole.TenantAdministrator => All,
        MembershipRole.OfficeManager => All.Except([ManageTenantSettings]).ToHashSet(StringComparer.Ordinal),
        MembershipRole.StaffUser => Read.Concat([InitiateOutboundCalls]).ToHashSet(StringComparer.Ordinal),
        MembershipRole.ReadOnlyUser => Read,
        _ => new HashSet<string>(StringComparer.Ordinal)
    };

    private static readonly HashSet<string> Read =
    [
        ViewLiveCalls, ViewRecentCalls, ViewTranscripts, ViewSummaries, ViewRecordingMetadata
    ];

    private static readonly HashSet<string> All =
    [
        ViewLiveCalls, ViewRecentCalls, ViewTranscripts, ViewSummaries, ViewRecordingMetadata,
        InitiateOutboundCalls, ManageTenantSettings, ManageLocationSettings, ViewAuditRecords
    ];
}

public sealed record IdentityUser(
    Guid Id,
    string ExternalSubject,
    string DisplayName,
    bool IsActive);

public sealed record UserMembership(
    Guid Id,
    Guid UserId,
    Guid TenantId,
    IReadOnlySet<Guid> LocationIds,
    MembershipRole Role,
    bool IsActive);
