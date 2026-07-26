using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Identity.Application;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Infrastructure;
using PurpleGlass.WebBff;
using PurpleGlass.Observability;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.WebBff", builder.Environment.EnvironmentName);

string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
SecurityOptions security = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new();
SafetyOptions safety = new()
{
    EnableRealTelephony = builder.Configuration.GetValue<bool>("Providers:EnableRealTelephony"),
    EnableRealAI = builder.Configuration.GetValue<bool>("Providers:EnableRealAI"),
    EnableRealSpeech = builder.Configuration.GetValue<bool>("Providers:EnableRealSpeech"),
    EnableOpenDental = builder.Configuration.GetValue<bool>("Integrations:EnableOpenDental"),
    AllowSensitiveData = builder.Configuration.GetValue<bool>("DataProtection:AllowSensitiveData")
};
ProductionSecurityValidator.Validate(security, safety, builder.Environment, builder.Configuration["AllowedHosts"] ?? string.Empty);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddSingleton(security);
IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection().SetApplicationName("PurpleGlass.WebBff");
if (builder.Environment.IsProduction())
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(security.DataProtectionKeysPath));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TrustedRequestContextAccessor>();
builder.Services.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<TrustedRequestContextAccessor>());
if (builder.Environment.IsDevelopment() && security.AllowDevelopmentAuthentication)
    builder.Services.AddSingleton<IIdentityDirectory, DevelopmentIdentityDirectory>();
else
    builder.Services.AddSingleton<IIdentityDirectory, ConfiguredIdentityDirectory>();
builder.Services.AddScoped<IdentityAuthorizationService>();

AuthenticationBuilder authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = security.AllowDevelopmentAuthentication
        ? CookieAuthenticationDefaults.AuthenticationScheme
        : OpenIdConnectDefaults.AuthenticationScheme;
});
authentication.AddCookie(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "PurpleGlass.Dev.Session" : security.CookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Clamp(security.SessionMinutes, 5, 480));
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => WriteSecurityError(context.Response, 401, "authentication_required");
    options.Events.OnRedirectToAccessDenied = context => WriteSecurityError(context.Response, 403, "access_denied");
});
if (!security.AllowDevelopmentAuthentication)
{
    authentication.AddOpenIdConnect(options =>
    {
        options.Authority = security.Oidc.Authority;
        options.ClientId = security.Oidc.ClientId;
        options.ClientSecret = security.Oidc.ClientSecret;
        options.CallbackPath = security.Oidc.CallbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
    });
}

builder.Services.AddPurpleGlassAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "PurpleGlass.Dev.Csrf" : "__Host-PurpleGlass.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) => new ValueTask(
        WriteSecurityError(context.HttpContext.Response, 429, "rate_limit_exceeded"));
    options.AddPolicy("security", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("sse", context => RateLimitPartition.GetConcurrencyLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 3, QueueLimit = 0 }));
});

if (builder.Environment.IsDevelopment() && security.DevelopmentOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("development", policy => policy
        .WithOrigins(security.DevelopmentOrigins).AllowCredentials().WithHeaders("Content-Type", "X-CSRF-TOKEN")
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));
}

builder.Services.AddTenancyInfrastructure(connectionString);
builder.Services.AddScoped<SecurityAuditService>();
builder.Services.AddScoped<TenancyService>();
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.AddConversationInfrastructure(connectionString);
builder.Services.AddSingleton<RealtimeEventHub>();
builder.Services.AddHostedService<MqttRealtimeSubscriber>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);
builder.Services.AddExceptionHandler<SecurityExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (app.Environment.IsDevelopment() && security.DevelopmentOrigins.Length > 0) app.UseCors("development");
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TrustedRequestContextMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

RouteGroupBuilder bff = app.MapGroup("/bff/v1");

bff.MapGet("/security/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
}).RequireRateLimiting("security");

bff.MapPost("/security/development-login", async (
    DevelopmentLoginRequest request, HttpContext context, IAntiforgery antiforgery,
    IIdentityDirectory directory, SecurityAuditService audit, CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment() || !security.AllowDevelopmentAuthentication)
        throw new SecurityBoundaryException("access_denied");
    await antiforgery.ValidateRequestAsync(context);
    string subject = request.User switch
    {
        "administrator" => DevelopmentIdentityDirectory.AdministratorSubject,
        "read-only" => DevelopmentIdentityDirectory.ReadOnlySubject,
        _ => throw new SecurityBoundaryException("authentication_required")
    };
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, subject),
        new Claim(SecurityClaimTypes.AuthenticationMethod, "development")
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    AuthorizedIdentity identity = await directory.ResolveAsync(subject, cancellationToken)
        ?? throw new SecurityBoundaryException("authentication_required");
    await audit.WriteAsync(identity.Membership.TenantId, identity.ActiveLocationId,
        identity.User.Id.ToString("D"), "Login", "BrowserSession", identity.User.Id.ToString("D"),
        "Allowed", "development_authentication", Guid.NewGuid(), cancellationToken);
    PurpleGlassTelemetry.SecurityAuthSuccess.Add(1, new KeyValuePair<string, object?>("method", "development"));
    SecurityLog.DevelopmentAuthenticationUsed(app.Logger);
    return Results.NoContent();
}).RequireRateLimiting("security");

RouteGroupBuilder protectedBff = bff.MapGroup(string.Empty).RequireAuthorization();
protectedBff.MapGet("/session", async (
    TrustedRequestContextAccessor accessor, IIdentityDirectory directory, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    AuthorizedIdentity identity = await directory.ResolveAsync(context.ExternalSubject, cancellationToken)
        ?? throw new SecurityBoundaryException("session_expired");
    return Results.Ok(new SessionProjection(
        identity.User.Id.ToString("D"), identity.User.DisplayName, identity.Membership.TenantId,
        identity.ActiveLocationId, identity.Membership.LocationIds, identity.Membership.Role.ToString(),
        identity.Permissions, context.AuthenticationMethod, context.AuthenticationMethod == "development"));
});
protectedBff.MapGet("/session/locations", (TrustedRequestContextAccessor accessor) =>
    Results.Ok(accessor.Current.AuthorizedLocationIds ?? new HashSet<Guid>()));
protectedBff.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery,
    TrustedRequestContextAccessor accessor, SecurityAuditService audit) =>
{
    await antiforgery.ValidateRequestAsync(context);
    RequestContext requestContext = accessor.Current;
    await audit.WriteAsync(requestContext.TenantId, requestContext.LocationId, requestContext.ActorId,
        "Logout", "BrowserSession", requestContext.UserId.ToString("D"), "Allowed", "user_logout",
        requestContext.CorrelationId, context.RequestAborted);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireRateLimiting("security");

protectedBff.MapGet("/tenant-summary", async (TenancyService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetCurrentSummaryAsync(cancellationToken)));
protectedBff.MapPut("/locations/{locationId:guid}/display-name", async (
    Guid locationId, UpdateLocationDisplayNameRequest request, HttpContext context, IAntiforgery antiforgery,
    TenancyService service, CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    return Results.Ok(await service.UpdateLocationDisplayNameAsync(locationId, request, cancellationToken));
}).RequireAuthorization(SecurityPolicies.ManageLocation).RequireRateLimiting("security");
protectedBff.MapGet("/calls", async (TrustedRequestContextAccessor accessor, CallManagementService calls,
    int? limit, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    return Results.Ok(await calls.GetRecentAsync(context.TenantId, context.LocationId,
        Math.Clamp(limit ?? 20, 1, 50), cancellationToken));
}).RequireAuthorization(SecurityPolicies.ViewCalls);
protectedBff.MapPost("/calls/outbound", async (SyntheticOutboundCallRequest request, HttpContext httpContext,
    IAntiforgery antiforgery, TrustedRequestContextAccessor accessor, CallManagementService calls,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(httpContext);
    if (!request.FromNumber.StartsWith("+1555", StringComparison.Ordinal)
        || !request.ToNumber.StartsWith("+1555", StringComparison.Ordinal))
        throw new ArgumentException("Only reserved synthetic phone numbers are accepted.");
    RequestContext context = accessor.Current;
    return Results.Ok(await calls.RequestOutboundAsync(new RequestOutboundCall(
        context.TenantId, context.LocationId, request.IdempotencyKey,
        request.FromNumber, request.ToNumber, context.CorrelationId), cancellationToken));
}).RequireAuthorization(SecurityPolicies.InitiateOutbound).RequireRateLimiting("security");
protectedBff.MapGet("/calls/{callId:guid}", async (
    Guid callId, TrustedRequestContextAccessor accessor, CallManagementService calls,
    ConversationService conversations, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    var call = await calls.GetForLocationAsync(context.TenantId, context.LocationId, callId, cancellationToken);
    return Results.Ok(new { call, conversation = await conversations.GetDetailsForCallAsync(context.TenantId, callId, cancellationToken) });
}).RequireAuthorization(SecurityPolicies.ViewTranscripts);
protectedBff.MapGet("/events", async (HttpContext httpContext, RealtimeEventHub hub,
    TrustedRequestContextAccessor accessor, CancellationToken cancellationToken) =>
{
    httpContext.Response.Headers.CacheControl = "no-cache, no-store";
    httpContext.Response.Headers.Connection = "keep-alive";
    httpContext.Response.ContentType = "text/event-stream";
    RequestContext context = accessor.Current;
    await using RealtimeSubscription subscription = hub.Subscribe(context.TenantId, context.LocationId);
    PurpleGlassTelemetry.ActiveSseConnections.Add(1);
    using var connectionActivity = PurpleGlassTelemetry.WebBff.StartActivity("sse.connection", System.Diagnostics.ActivityKind.Server);
    try
    {
        await foreach (RealtimeEvent realtimeEvent in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            using var delivery = PurpleGlassTelemetry.WebBff.StartActivity("sse.deliver", System.Diagnostics.ActivityKind.Producer);
            delivery?.SetTag("purpleglass.correlation_id", realtimeEvent.CorrelationId);
            await httpContext.Response.WriteAsync($"id: {realtimeEvent.CorrelationId:D}\n", cancellationToken);
            await httpContext.Response.WriteAsync($"event: {realtimeEvent.EventType}\n", cancellationToken);
            await httpContext.Response.WriteAsync($"data: {realtimeEvent.Payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }
    finally
    {
        PurpleGlassTelemetry.ActiveSseConnections.Add(-1);
    }
}).RequireAuthorization(SecurityPolicies.ViewCalls).RequireRateLimiting("sse");

app.Run();

static Task WriteSecurityError(HttpResponse response, int status, string code)
{
    response.StatusCode = status;
    response.ContentType = "application/problem+json";
    return response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = "Security request rejected", Extensions = { ["code"] = code } });
}

public partial class Program;

namespace PurpleGlass.WebBff
{
    public sealed record DevelopmentLoginRequest(string User);
    public sealed record SyntheticOutboundCallRequest(string IdempotencyKey, string FromNumber, string ToNumber);

    public static partial class SecurityLog
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
            Message = "Development authentication used for a synthetic identity.")]
        public static partial void DevelopmentAuthenticationUsed(ILogger logger);
    }

    public sealed class SecurityExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            (int status, string title, string code) = exception switch
            {
                SecurityBoundaryException securityException => securityException.Code switch
                {
                    "authentication_required" or "session_expired" or "membership_inactive" => (401, "Authentication required", securityException.Code),
                    "location_access_denied" or "tenant_access_denied" or "access_denied" => (403, "Access denied", securityException.Code),
                    _ => (403, "Access denied", securityException.Code)
                },
                AntiforgeryValidationException => (400, "CSRF validation failed", "csrf_validation_failed"),
                TenancyResourceNotFoundException => (404, "Resource not found", "resource_not_found"),
                TenancyConcurrencyException => (409, "Resource changed", "concurrency_conflict"),
                CallApplicationException { Code: "call_not_found" } => (404, "Resource not found", "resource_not_found"),
                ConversationApplicationException { Code: "conversation_not_found" } => (404, "Resource not found", "resource_not_found"),
                ArgumentException => (400, "Invalid request", "invalid_request"),
                _ => (500, "An unexpected error occurred", "unexpected_error")
            };
            if (code == "csrf_validation_failed") PurpleGlassTelemetry.SecurityCsrfFailure.Add(1);
            else if (status == 401) PurpleGlassTelemetry.SecurityAuthFailure.Add(1);
            else if (status == 403) PurpleGlassTelemetry.SecurityAuthorizationDenied.Add(1);
            if (httpContext.User.Identity?.IsAuthenticated == true && status is 403 or 404)
            {
                SecurityAuditService audit = httpContext.RequestServices.GetRequiredService<SecurityAuditService>();
                TrustedRequestContextAccessor accessor = httpContext.RequestServices.GetRequiredService<TrustedRequestContextAccessor>();
                RequestContext request = accessor.Current;
                await audit.WriteAsync(request.TenantId, request.LocationId, request.ActorId,
                    "AccessDenied", "BffRoute", httpContext.Request.Path,
                    "Denied", code, request.CorrelationId, cancellationToken);
            }
            httpContext.Response.StatusCode = status;
            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails { Status = status, Title = title, Extensions = { ["code"] = code } },
                Exception = exception
            });
        }
    }
}
