using Microsoft.AspNetCore.Authorization;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Modules.Identity.Domain;
using PurpleGlass.Modules.Audit.Application;

namespace PurpleGlass.WebBff;

public static class SecurityPolicies
{
    public const string ViewCalls = "ViewCalls";
    public const string ViewTranscripts = "ViewTranscripts";
    public const string ManageLocation = "ManageLocation";
    public const string InitiateOutbound = "InitiateOutbound";

    public static IServiceCollection AddPurpleGlassAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ViewCalls, policy => policy.RequireAuthenticatedUser().AddRequirements(
                new PermissionRequirement(SecurityPermissions.ViewRecentCalls)));
            options.AddPolicy(ViewTranscripts, policy => policy.RequireAuthenticatedUser().AddRequirements(
                new PermissionRequirement(SecurityPermissions.ViewTranscripts)));
            options.AddPolicy(ManageLocation, policy => policy.RequireAuthenticatedUser().AddRequirements(
                new PermissionRequirement(SecurityPermissions.ManageLocationSettings)));
            options.AddPolicy(InitiateOutbound, policy => policy.RequireAuthenticatedUser().AddRequirements(
                new PermissionRequirement(SecurityPermissions.InitiateOutboundCalls)));
        });
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    SecurityAuditService audit)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        HttpContext? httpContext = httpContextAccessor.HttpContext;
        TrustedRequestContextAccessor? accessor = httpContext?.RequestServices
            .GetService<TrustedRequestContextAccessor>();
        if (accessor?.Current.HasPermission(requirement.Permission) == true)
        {
            context.Succeed(requirement);
            return;
        }

        if (accessor is not null && httpContext is not null)
        {
            RequestContext request = accessor.Current;
            await audit.WriteAsync(request.TenantId, request.LocationId, request.ActorId,
                "AccessDenied", "BffRoute", httpContext.Request.Path,
                "Denied", "permission_missing", request.CorrelationId, httpContext.RequestAborted);
        }
    }
}
