namespace PurpleGlass.Application.Abstractions;

public sealed record RequestContext(
    Guid TenantId,
    Guid LocationId,
    string ActorId,
    string Role,
    Guid CorrelationId,
    Guid UserId = default,
    Guid MembershipId = default,
    string ExternalSubject = "",
    IReadOnlySet<Guid>? AuthorizedLocationIds = null,
    IReadOnlySet<string>? Permissions = null,
    string AuthenticationMethod = "unknown")
{
    public bool HasPermission(string permission) => Permissions?.Contains(permission) == true;

    public bool CanAccessLocation(Guid locationId) =>
        AuthorizedLocationIds?.Contains(locationId) == true;
}

public interface IRequestContextAccessor
{
    RequestContext Current { get; }
}
