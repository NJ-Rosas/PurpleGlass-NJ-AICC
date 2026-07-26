using System.Security.Claims;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Modules.Identity.Application;
using PurpleGlass.Modules.Identity.Domain;

namespace PurpleGlass.WebBff;

public sealed class WebBffAssembly;

public static class SecurityClaimTypes
{
    public const string AuthenticationMethod = "pg:authentication_method";
}

public sealed class DevelopmentIdentityDirectory : IIdentityDirectory
{
    public const string AdministratorSubject = "dev-admin";
    public const string ReadOnlySubject = "dev-readonly";
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid LocationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly IReadOnlyDictionary<string, AuthorizedIdentity> Identities =
        new Dictionary<string, AuthorizedIdentity>(StringComparer.Ordinal)
        {
            [AdministratorSubject] = Create(
                AdministratorSubject, "Nilve Prototype Administrator", MembershipRole.TenantAdministrator),
            [ReadOnlySubject] = Create(
                ReadOnlySubject, "Riley Prototype Reader", MembershipRole.ReadOnlyUser)
        };

    public Task<AuthorizedIdentity?> ResolveAsync(string externalSubject, CancellationToken cancellationToken) =>
        Task.FromResult(Identities.GetValueOrDefault(externalSubject));

    private static AuthorizedIdentity Create(string subject, string displayName, MembershipRole role)
    {
        var user = new IdentityUser(DeterministicGuid(subject), subject, displayName, true);
        var membership = new UserMembership(
            DeterministicGuid($"membership:{subject}"), user.Id, TenantId,
            new HashSet<Guid> { LocationId }, role, true);
        return new AuthorizedIdentity(user, membership, LocationId, SecurityPermissions.ForRole(role));
    }

    private static Guid DeterministicGuid(string value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed class ConfiguredIdentityDirectory(SecurityOptions options) : IIdentityDirectory
{
    public Task<AuthorizedIdentity?> ResolveAsync(string externalSubject, CancellationToken cancellationToken)
    {
        IdentityMappingOptions? mapping = options.IdentityMappings.SingleOrDefault(candidate =>
            string.Equals(candidate.ExternalSubject, externalSubject, StringComparison.Ordinal));
        if (mapping is null || !Enum.TryParse(mapping.Role, true, out MembershipRole role))
            return Task.FromResult<AuthorizedIdentity?>(null);

        var user = new IdentityUser(mapping.UserId, mapping.ExternalSubject, mapping.DisplayName, mapping.Active);
        var membership = new UserMembership(mapping.MembershipId, mapping.UserId, mapping.TenantId,
            mapping.LocationIds.ToHashSet(), role, mapping.Active);
        return Task.FromResult<AuthorizedIdentity?>(new(
            user, membership, mapping.ActiveLocationId, SecurityPermissions.ForRole(role)));
    }
}

public sealed class TrustedRequestContextAccessor : IRequestContextAccessor
{
    private RequestContext? current;
    public RequestContext Current => current ?? throw new SecurityBoundaryException("authentication_required");
    public void Set(RequestContext value) => current = value;
}

public sealed class TrustedRequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IdentityAuthorizationService authorization,
        TrustedRequestContextAccessor accessor)
    {
        Guid correlationId = PurpleGlass.Observability.CorrelationIds.PreserveOrCreate(
            Guid.TryParse(httpContext.Request.Headers["X-Correlation-Id"], out Guid supplied) ? supplied : null);
        System.Diagnostics.Activity.Current?.SetTag("purpleglass.correlation_id", correlationId);
        httpContext.Response.Headers["X-Correlation-Id"] = correlationId.ToString("D");

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            string subject = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new SecurityBoundaryException("authentication_required");
            AuthorizedIdentity identity;
            try
            {
                identity = await authorization.RequireActiveAsync(subject, null, httpContext.RequestAborted);
            }
            catch (IdentitySecurityException exception)
            {
                throw new SecurityBoundaryException(exception.Code);
            }

            accessor.Set(new RequestContext(
                identity.Membership.TenantId,
                identity.ActiveLocationId,
                identity.User.Id.ToString("D"),
                identity.Membership.Role.ToString(),
                correlationId,
                identity.User.Id,
                identity.Membership.Id,
                identity.User.ExternalSubject,
                identity.Membership.LocationIds,
                identity.Permissions,
                httpContext.User.FindFirstValue(SecurityClaimTypes.AuthenticationMethod) ?? "oidc"));
        }

        await next(httpContext);
    }
}

public sealed class SecurityBoundaryException(string code) : Exception("The requested operation could not be completed.")
{
    public string Code { get; } = code;
}

public sealed record SessionProjection(
    string UserId,
    string DisplayName,
    Guid TenantId,
    Guid ActiveLocationId,
    IReadOnlySet<Guid> AuthorizedLocationIds,
    string Role,
    IReadOnlySet<string> Permissions,
    string AuthenticationMethod,
    bool DevelopmentAuthentication);
