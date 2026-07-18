using PurpleGlass.Application.Abstractions;

namespace PurpleGlass.WebBff;

public sealed class PrototypeSessionOptions
{
    public const string SectionName = "PrototypeSession";

    public bool Enabled { get; init; }

    public Guid TenantId { get; init; }

    public Guid LocationId { get; init; }

    public string ActorId { get; init; } = "prototype-user";

    public string Role { get; init; } = "OfficeAdministrator";
}

public sealed class PrototypeRequestContextAccessor : IRequestContextAccessor
{
    private RequestContext? current;

    public RequestContext Current => current
        ?? throw new InvalidOperationException("No request context has been established.");

    public void Set(RequestContext requestContext) => current = requestContext;
}

public sealed class PrototypeRequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        PrototypeSessionOptions options,
        PrototypeRequestContextAccessor accessor)
    {
        if (options.Enabled)
        {
            Guid correlationId = Guid.TryParse(
                httpContext.Request.Headers["X-Correlation-Id"],
                out Guid suppliedCorrelationId)
                ? suppliedCorrelationId
                : Guid.NewGuid();

            httpContext.Response.Headers["X-Correlation-Id"] = correlationId.ToString("D");
            accessor.Set(new RequestContext(
                options.TenantId,
                options.LocationId,
                options.ActorId,
                options.Role,
                correlationId));
        }

        await next(httpContext);
    }
}
