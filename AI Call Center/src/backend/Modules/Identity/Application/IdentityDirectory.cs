using PurpleGlass.Modules.Identity.Domain;

namespace PurpleGlass.Modules.Identity.Application;

public interface IIdentityDirectory
{
    Task<AuthorizedIdentity?> ResolveAsync(string externalSubject, CancellationToken cancellationToken);
}

public sealed record AuthorizedIdentity(
    IdentityUser User,
    UserMembership Membership,
    Guid ActiveLocationId,
    IReadOnlySet<string> Permissions);

public sealed class IdentityAuthorizationService(IIdentityDirectory directory)
{
    public async Task<AuthorizedIdentity> RequireActiveAsync(
        string externalSubject,
        Guid? requestedLocationId,
        CancellationToken cancellationToken)
    {
        AuthorizedIdentity identity = await directory.ResolveAsync(externalSubject, cancellationToken)
            ?? throw new IdentitySecurityException("authentication_required");

        if (!identity.User.IsActive || !identity.Membership.IsActive)
        {
            throw new IdentitySecurityException("membership_inactive");
        }

        if (requestedLocationId is Guid locationId && !identity.Membership.LocationIds.Contains(locationId))
        {
            throw new IdentitySecurityException("location_access_denied");
        }

        return requestedLocationId is null
            ? identity
            : identity with { ActiveLocationId = requestedLocationId.Value };
    }
}

public sealed class IdentitySecurityException(string code) : Exception("The requested security operation was denied.")
{
    public string Code { get; } = code;
}
