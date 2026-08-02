using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
using PurpleGlass.Eventing.Infrastructure;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.CallManagement.Contracts;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
if (int.TryParse(builder.Configuration["PORT"], out int renderPort) && renderPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.WebBff", builder.Environment.EnvironmentName);

string connectionString = builder.Configuration.RequireConnectionString();
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
if (!string.IsNullOrWhiteSpace(security.DataProtectionKeysPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(security.DataProtectionKeysPath));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient(nameof(WorkerRuntimeGateway), client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<WorkerRuntimeGateway>();
builder.Services.AddHostedService(service => service.GetRequiredService<WorkerRuntimeGateway>());
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
    options.Cookie.SecurePolicy = !builder.Environment.IsDevelopment() || security.ForceSecureCookies
        ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
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
    options.Cookie.SecurePolicy = !builder.Environment.IsDevelopment() || security.ForceSecureCookies
        ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) => new ValueTask(
        WriteSecurityError(context.HttpContext.Response, 429, "rate_limit_exceeded"));
    options.AddPolicy("security", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Environment.IsDevelopment() ? 300 : 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("sse", context => RateLimitPartition.GetConcurrencyLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 3, QueueLimit = 0 }));
    options.AddPolicy("telephony-webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("telephony-media", context => RateLimitPartition.GetConcurrencyLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 50, QueueLimit = 0 }));
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
builder.Services.AddSingleton<ILocationCallLanguageResolver, ScopedLocationCallLanguageResolver>();
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.AddConversationInfrastructure(connectionString);
builder.Services.AddEventingInfrastructure(connectionString);
string telephonyProvider = builder.Configuration["Telephony:Provider"] ?? "None";
bool realTelephonyEnabled = builder.Configuration.GetValue<bool>("Providers:EnableRealTelephony");
TelephonyProviderConfigurationValidator.Validate(telephonyProvider, realTelephonyEnabled);
if (realTelephonyEnabled && telephonyProvider.Equals("Twilio", StringComparison.OrdinalIgnoreCase))
{
    var twilioOptions = new TwilioTelephonyOptions
    {
        AccountSid = builder.Configuration["Telephony:Twilio:AccountSid"] ?? string.Empty,
        AuthToken = builder.Configuration["Telephony:Twilio:AuthToken"] ?? string.Empty,
        PublicBaseUrl = builder.Configuration["Telephony:PublicBaseUrl"] ?? string.Empty,
    };
    builder.Services.AddSingleton(twilioOptions);
    builder.Services.AddSingleton<ITelephonyProvider, TwilioTelephonyProvider>();
    builder.Services.AddSingleton<ITelephonyWebhookVerifier, TwilioWebhookVerifier>();
}
else if (builder.Environment.IsDevelopment() && telephonyProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<FakeTelephonyProvider>();
    builder.Services.AddSingleton<ITelephonyProvider>(service => service.GetRequiredService<FakeTelephonyProvider>());
    builder.Services.AddSingleton<ITelephonyWebhookVerifier>(service => service.GetRequiredService<FakeTelephonyProvider>());
}
else
{
    builder.Services.AddSingleton<DisabledTelephonyProvider>();
    builder.Services.AddSingleton<ITelephonyProvider>(service => service.GetRequiredService<DisabledTelephonyProvider>());
    builder.Services.AddSingleton<ITelephonyWebhookVerifier>(service => service.GetRequiredService<DisabledTelephonyProvider>());
}
builder.Services.AddSingleton<RealtimeEventHub>();
builder.Services.AddRealtimeVoice(builder.Configuration);
builder.Services.AddHostedService<MqttRealtimeSubscriber>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<TelephonyHealthCheck>("telephony", tags: ["ready"]);
builder.Services.AddExceptionHandler<SecurityExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();
TwilioRealtimeAudioOptions startupTransportOptions = app.Services.GetRequiredService<TwilioRealtimeAudioOptions>();
string applicationVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";
Action<ILogger, string, double, int, double, int, bool, Exception?> logVoiceTransportConfigured =
    LoggerMessage.Define<string, double, int, double, int, bool>(
        LogLevel.Information,
        new EventId(208, "RealtimeVoiceTransportConfigured"),
        "Realtime voice transport configured; ApplicationVersion={ApplicationVersion}, OutboundPacketDurationMs={OutboundPacketDurationMs}, OutboundPacketBytes={OutboundPacketBytes}, StartupBufferDurationMs={StartupBufferDurationMs}, MaxOutboundMediaBytes={MaxOutboundMediaBytes}, PacingEnabled={PacingEnabled}, PacingMode=monotonic_bounded_low_high_reserve.");
logVoiceTransportConfigured(
    app.Logger, applicationVersion, startupTransportOptions.OutboundPacketDuration.TotalMilliseconds,
    startupTransportOptions.OutboundPacketBytes,
    startupTransportOptions.OutboundStartupBufferDuration.TotalMilliseconds,
    startupTransportOptions.MaxOutboundMediaBytes, startupTransportOptions.EnableOutboundPacing,
    null);
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (app.Environment.IsDevelopment() && security.DevelopmentOrigins.Length > 0) app.UseCors("development");
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseMiddleware<TrustedRequestContextMiddleware>();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false, ResponseWriter = WriteHealthResponse });
app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse,
    ResultStatusCodes =
    {
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
    },
});

app.Map("/telephony/twilio/media", async (
    HttpContext httpContext,
    ITelephonyWebhookVerifier verifier,
    TwilioRealtimeAudioTransportFactory transportFactory,
    TwilioRealtimeAudioOptions transportOptions,
    RealtimeVoiceOptions voiceOptions,
    CallManagementService calls,
    VoiceSessionManager sessions) =>
{
    if (!httpContext.WebSockets.IsWebSocketRequest)
    {
        await WriteSecurityError(httpContext.Response, 400, "provider_websocket_required");
        return;
    }
    if (!VerifyTelephonyMediaRequest(httpContext, verifier, builder.Configuration))
    {
        await WriteSecurityError(httpContext.Response, 403, "invalid_provider_signature");
        return;
    }
    if (!voiceOptions.Enabled)
    {
        await WriteSecurityError(httpContext.Response, 503, "voice_disabled");
        return;
    }

    string expectedAccountSid = builder.Configuration["Telephony:Twilio:AccountSid"] ?? string.Empty;
    using WebSocket socket = await httpContext.WebSockets.AcceptWebSocketAsync();
    TwilioRealtimeAudioTransport? transport = null;
    bool managerOwnsTransport = false;
    try
    {
        transport = await transportFactory.InitializeAsync(
            socket, expectedAccountSid, transportOptions, httpContext.RequestAborted);
        VoiceCallContext call = await calls.ConnectVoiceMediaAsync(
            transport.Provider, transport.ProviderCallId, httpContext.RequestAborted);
        managerOwnsTransport = true;
        await sessions.RunAsync(call, transport, httpContext.RequestAborted);
    }
    catch (TwilioRealtimeAudioException exception)
    {
        await CloseMediaSocketAsync(socket, exception.Code, httpContext.RequestAborted);
    }
    catch (CallApplicationException exception)
    {
        await CloseMediaSocketAsync(socket, exception.Code, httpContext.RequestAborted);
    }
    catch (VoiceSessionConflictException exception)
    {
        await CloseMediaSocketAsync(socket, exception.Code, httpContext.RequestAborted);
    }
    finally
    {
        if (!managerOwnsTransport && transport is not null)
            await transport.DisposeAsync();
    }
}).RequireRateLimiting("telephony-media");

app.MapPost("/telephony/twilio/inbound", async (HttpContext httpContext,
    ITelephonyWebhookVerifier verifier, CallManagementService calls, WorkerRuntimeGateway workerRuntime,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.Request.HasFormContentType)
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_content_type_invalid" });
    IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken);
    Dictionary<string, string> parameters = FormValues(form);
    if (!VerifyTelephonyRequest(httpContext, verifier, builder.Configuration, parameters))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "invalid_provider_signature" });
    if (!Required(parameters, "AccountSid", out _)
        || !Required(parameters, "CallSid", out string callSid)
        || !Required(parameters, "From", out string from)
        || !Required(parameters, "To", out string to))
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_metadata_missing" });
    if (!AccountSidMatches(parameters, builder.Configuration))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "provider_account_mismatch" });

    using Activity? activity = PurpleGlassTelemetry.Integrations.StartActivity("telephony.inbound.receive", ActivityKind.Consumer);
    workerRuntime.RequestWake();
    Guid correlationId = Guid.NewGuid();
    activity?.SetTag("purpleglass.correlation_id", correlationId);
    _ = await calls.RegisterInboundTransportAsync("Twilio", callSid,
        parameters.GetValueOrDefault("ParentCallSid"), from, to, correlationId, cancellationToken);
    PurpleGlassTelemetry.TelephonyInboundCalls.Add(1, new KeyValuePair<string, object?>("provider", "Twilio"));
    return Results.Text(BuildMediaStreamTwiml(builder.Configuration), "application/xml");
}).RequireRateLimiting("telephony-webhook");

app.MapPost("/telephony/twilio/answer", async (Guid? operationId, HttpContext httpContext,
    ITelephonyWebhookVerifier verifier, CallManagementService calls, CancellationToken cancellationToken) =>
{
    if (!httpContext.Request.HasFormContentType)
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_content_type_invalid" });
    IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken);
    Dictionary<string, string> parameters = FormValues(form);
    if (!VerifyTelephonyRequest(httpContext, verifier, builder.Configuration, parameters))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "invalid_provider_signature" });
    if (!operationId.HasValue
        || !Required(parameters, "AccountSid", out _)
        || !Required(parameters, "CallSid", out string callSid))
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_metadata_missing" });
    if (!AccountSidMatches(parameters, builder.Configuration))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "provider_account_mismatch" });
    await calls.ReconcileProviderIdentityAsync(operationId.Value, "Twilio", callSid, cancellationToken);
    return Results.Text(BuildMediaStreamTwiml(builder.Configuration), "application/xml");
}).RequireRateLimiting("telephony-webhook");

app.MapPost("/telephony/twilio/status", async (Guid? operationId, HttpContext httpContext,
    ITelephonyWebhookVerifier verifier, CallManagementService calls, VoiceSessionManager sessions,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.Request.HasFormContentType)
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_content_type_invalid" });
    IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken);
    Dictionary<string, string> parameters = FormValues(form);
    PurpleGlassTelemetry.TelephonyWebhooksReceived.Add(1, new KeyValuePair<string, object?>("provider", "Twilio"));
    if (!VerifyTelephonyRequest(httpContext, verifier, builder.Configuration, parameters))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "invalid_provider_signature" });
    if (!Required(parameters, "AccountSid", out _)
        || !Required(parameters, "CallSid", out string callSid) || !Required(parameters, "CallStatus", out string status))
        return Results.Problem(statusCode: 400, title: "Invalid provider request", extensions: new Dictionary<string, object?> { ["code"] = "provider_metadata_missing" });
    if (!AccountSidMatches(parameters, builder.Configuration))
        return Results.Problem(statusCode: 403, title: "Provider request rejected", extensions: new Dictionary<string, object?> { ["code"] = "provider_account_mismatch" });
    if (operationId.HasValue) await calls.ReconcileProviderIdentityAsync(operationId.Value, "Twilio", callSid, cancellationToken);
    string eventId = httpContext.Request.Headers["I-Twilio-Idempotency-Token"].FirstOrDefault()
        ?? $"{callSid}:{status.ToLowerInvariant()}";
    CallSummary call = await calls.ApplyProviderStatusAsync(new ApplyProviderCallStatus("Twilio", callSid, status, eventId), cancellationToken);
    if (call.State is "Completed" or "Failed")
        _ = sessions.RequestStop(call.CallId, $"provider_{status.ToLowerInvariant().Replace('-', '_')}");
    if (status.Equals("in-progress", StringComparison.OrdinalIgnoreCase)) PurpleGlassTelemetry.TelephonyCallsConnected.Add(1);
    if (status is "failed" or "busy" or "no-answer" or "canceled") PurpleGlassTelemetry.TelephonyCallsFailed.Add(1);
    return Results.NoContent();
}).RequireRateLimiting("telephony-webhook");

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
protectedBff.MapPut("/locations/{locationId:guid}/default-call-language", async (
    Guid locationId, UpdateLocationDefaultCallLanguageRequest request, HttpContext context, IAntiforgery antiforgery,
    TenancyService service, CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    return Results.Ok(await service.UpdateLocationDefaultCallLanguageAsync(locationId, request, cancellationToken));
}).RequireAuthorization(SecurityPolicies.ManageLocation).RequireRateLimiting("security");
protectedBff.MapGet("/calls", async (TrustedRequestContextAccessor accessor, CallManagementService calls,
    int? limit, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    return Results.Ok(await calls.GetRecentAsync(context.TenantId, context.LocationId,
        Math.Clamp(limit ?? 20, 1, 50), cancellationToken));
}).RequireAuthorization(SecurityPolicies.ViewCalls);
protectedBff.MapPost("/calls/outbound", async (OutboundTransportRequest request, HttpContext httpContext,
    IAntiforgery antiforgery, TrustedRequestContextAccessor accessor, CallManagementService calls,
    SecurityAuditService audit, ITelephonyProvider provider, RealtimeVoiceRuntimeStatus voiceRuntime,
    WorkerRuntimeGateway workerRuntime,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(httpContext);
    if (provider.Status.Enabled
        && provider.Name.Equals("Twilio", StringComparison.OrdinalIgnoreCase)
        && (!provider.Status.Configured || !voiceRuntime.Ready))
        throw new CallApplicationException("telephony_runtime_unavailable",
            "The telephony realtime voice runtime is unavailable.");
    RequestContext context = accessor.Current;
    if (context.AuthorizedLocationIds?.Contains(request.LocationId) != true)
        throw new SecurityBoundaryException("location_access_denied");
    await workerRuntime.EnsureReadyAsync(cancellationToken);
    CallSummary call = await calls.RequestTransportOutboundAsync(new RequestTransportOutboundCall(
        context.TenantId, request.LocationId, request.IdempotencyKey,
        request.DestinationNumber, context.CorrelationId, LanguageCode: request.LanguageCode), cancellationToken);
    await audit.WriteAsync(context.TenantId, request.LocationId, context.ActorId, "OutboundCallRequested",
        "CallSession", call.CallId.ToString("D"), "Allowed", "telephony_transport", context.CorrelationId, cancellationToken);
    return Results.Accepted($"/bff/v1/calls/{call.CallId:D}", call);
}).RequireAuthorization(SecurityPolicies.InitiateOutbound).RequireRateLimiting("security");
protectedBff.MapPost("/calls/{callId:guid}/hangup", async (Guid callId, HttpContext httpContext,
    IAntiforgery antiforgery, TrustedRequestContextAccessor accessor, CallManagementService calls,
    SecurityAuditService audit, VoiceSessionManager sessions, CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(httpContext);
    RequestContext context = accessor.Current;
    CallSummary call = await calls.RequestHangupAsync(new RequestCallHangup(
        context.TenantId, context.LocationId, callId), cancellationToken);
    await audit.WriteAsync(context.TenantId, context.LocationId, context.ActorId, "CallHangupRequested",
        "CallSession", callId.ToString("D"), "Allowed", "telephony_transport", context.CorrelationId, cancellationToken);
    _ = sessions.RequestStop(callId, "purpleglass_hangup");
    return Results.Accepted($"/bff/v1/calls/{callId:D}", call);
}).RequireAuthorization(SecurityPolicies.InitiateOutbound).RequireRateLimiting("security");
protectedBff.MapGet("/telephony/status", (ITelephonyProvider provider, RealtimeVoiceRuntimeStatus voiceRuntime) =>
{
    TelephonyProviderStatus status = provider.Status;
    bool requiresRealtimeVoice = status.Enabled
        && provider.Name.Equals("Twilio", StringComparison.OrdinalIgnoreCase);
    bool configured = status.Configured && (!requiresRealtimeVoice || voiceRuntime.Ready);
    string state = requiresRealtimeVoice && status.Configured && !voiceRuntime.Ready ? voiceRuntime.State : status.State;
    return Results.Ok(new TelephonyConfigurationStatus(provider.Name, status.Enabled, configured, state));
});
protectedBff.MapGet("/telephony/numbers", async (TrustedRequestContextAccessor accessor,
    CallManagementService calls, CancellationToken cancellationToken) =>
    Results.Ok(await calls.GetTelephonyNumbersAsync(accessor.Current.TenantId, cancellationToken)))
    .RequireAuthorization(SecurityPolicies.ManageLocation);
protectedBff.MapPut("/telephony/numbers", async (TelephonyNumberRequest request, HttpContext httpContext,
    IAntiforgery antiforgery, TrustedRequestContextAccessor accessor, CallManagementService calls,
    SecurityAuditService audit, CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(httpContext);
    RequestContext context = accessor.Current;
    if (request.LocationId.HasValue && context.AuthorizedLocationIds?.Contains(request.LocationId.Value) != true)
        throw new SecurityBoundaryException("location_access_denied");
    TelephonyNumberSummary number = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
        context.TenantId, request.LocationId, request.Provider, request.Number, request.ProviderNumberId,
        request.InboundEnabled, request.OutboundEnabled, request.Active), cancellationToken);
    await audit.WriteAsync(context.TenantId, request.LocationId ?? context.LocationId, context.ActorId, "TelephonyNumberConfigured",
        "TelephonyNumber", number.Id.ToString("D"), "Allowed", "configuration_changed", context.CorrelationId, cancellationToken);
    return Results.Ok(number);
}).RequireAuthorization(SecurityPolicies.ManageLocation).RequireRateLimiting("security");
protectedBff.MapGet("/calls/{callId:guid}", async (
    Guid callId, TrustedRequestContextAccessor accessor, CallManagementService calls,
    ConversationService conversations, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    var call = await calls.GetForLocationAsync(context.TenantId, context.LocationId, callId, cancellationToken);
    return Results.Ok(new { call, conversation = await conversations.GetDetailsForCallAsync(context.TenantId, callId, cancellationToken) });
}).RequireAuthorization(SecurityPolicies.ViewTranscripts);
protectedBff.MapGet("/operations/dead-letters", async (
    [AsParameters] DeadLetterListRequest request, TrustedRequestContextAccessor accessor,
    DeadLetterOperationsService service, CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    return Results.Ok(await service.QueryAsync(new DeadLetterQuery(
        context.TenantId, context.AuthorizedLocationIds ?? new HashSet<Guid>(), request.LocationId,
        request.MessageType, request.FailureCategory, request.FromUtc, request.ToUtc,
        request.MessageId, request.CorrelationId, request.TraceId, request.Page ?? 1, request.PageSize ?? 20), cancellationToken));
}).RequireAuthorization(SecurityPolicies.ViewDeadLetters);
protectedBff.MapGet("/operations/dead-letters/{messageId:guid}", async (
    Guid messageId, TrustedRequestContextAccessor accessor, DeadLetterOperationsService service,
    CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    DeadLetterDetail? detail = await service.GetAsync(context.TenantId,
        context.AuthorizedLocationIds ?? new HashSet<Guid>(), messageId, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
}).RequireAuthorization(SecurityPolicies.ViewDeadLetters);
protectedBff.MapPost("/operations/dead-letters/{messageId:guid}/retry", async (
    Guid messageId, HttpContext httpContext, IAntiforgery antiforgery,
    TrustedRequestContextAccessor accessor, DeadLetterOperationsService service,
    SecurityAuditService audit, RealtimeEventHub realtime, CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(httpContext);
    RequestContext context = accessor.Current;
    DeadLetterRecoveryResult result = await service.RequeueAsync(context.TenantId,
        context.AuthorizedLocationIds ?? new HashSet<Guid>(), messageId,
        context.ActorId, context.CorrelationId, cancellationToken);
    string decision = result == DeadLetterRecoveryResult.Recovered ? "Allowed" : "Denied";
    await audit.WriteAsync(context.TenantId, context.LocationId, context.ActorId,
        result == DeadLetterRecoveryResult.Recovered ? "DeadLetterRequeued" : "DeadLetterRecoveryRejected",
        "OutboxMessage", messageId.ToString("D"), decision, result.ToString(), context.CorrelationId, cancellationToken);
    if (result == DeadLetterRecoveryResult.Recovered)
    {
        string payload = JsonSerializer.Serialize(new { messageId, status = "Pending", correlationId = context.CorrelationId });
        realtime.Publish(new RealtimeEvent(context.TenantId, context.LocationId, context.CorrelationId,
            messageId, "dead-letter-recovered", payload, System.Diagnostics.Activity.Current?.Id,
            System.Diagnostics.Activity.Current?.TraceStateString));
        return Results.Accepted($"/bff/v1/operations/dead-letters/{messageId}", new { result = "Recovered" });
    }
    return result == DeadLetterRecoveryResult.NotFound
        ? Results.NotFound()
        : Results.Conflict(new ProblemDetails { Status = 409, Title = "Dead letter is not recoverable", Extensions = { ["code"] = "not_dead_lettered" } });
}).RequireAuthorization(SecurityPolicies.RecoverDeadLetters).RequireRateLimiting("security");
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

app.MapFallbackToFile("index.html");
app.Run();

static Task WriteSecurityError(HttpResponse response, int status, string code)
{
    response.StatusCode = status;
    response.ContentType = "application/problem+json";
    return response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = "Security request rejected", Extensions = { ["code"] = code } });
}

static Dictionary<string, string> FormValues(IFormCollection form) =>
    form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

static bool Required(IReadOnlyDictionary<string, string> values, string key, out string value)
{
    if (values.TryGetValue(key, out string? found) && !string.IsNullOrWhiteSpace(found))
    {
        value = found;
        return true;
    }
    value = string.Empty;
    return false;
}

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(
            pair => pair.Key,
            pair => new
            {
                status = pair.Value.Status.ToString(),
                description = pair.Value.Description,
                data = pair.Value.Data,
            },
            StringComparer.Ordinal),
    }, context.RequestAborted);
}

static bool AccountSidMatches(IReadOnlyDictionary<string, string> parameters, IConfiguration configuration) =>
    parameters.TryGetValue("AccountSid", out string? received)
    && !string.IsNullOrWhiteSpace(received)
    && string.Equals(received, configuration["Telephony:Twilio:AccountSid"], StringComparison.Ordinal);

static string BuildMediaStreamTwiml(IConfiguration configuration)
{
    string configuredBase = configuration["Telephony:PublicBaseUrl"] ?? string.Empty;
    if (!Uri.TryCreate(configuredBase, UriKind.Absolute, out Uri? publicBase)
        || publicBase.Scheme != Uri.UriSchemeHttps)
        throw new CallApplicationException("public_base_url_invalid", "The telephony public base URL is invalid.");
    var builder = new UriBuilder(new Uri(publicBase, "/telephony/twilio/media"))
    {
        Scheme = "wss",
        Port = -1,
        Query = string.Empty,
    };
    string streamUrl = SecurityElement.Escape(builder.Uri.ToString()) ?? string.Empty;
    return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Connect><Stream url=\"{streamUrl}\" /></Connect></Response>";
}

static bool VerifyTelephonyRequest(HttpContext context, ITelephonyWebhookVerifier verifier,
    IConfiguration configuration, IReadOnlyDictionary<string, string> parameters)
{
    string? signature = context.Request.Headers["X-Twilio-Signature"].FirstOrDefault();
    string configuredBase = configuration["Telephony:PublicBaseUrl"] ?? string.Empty;
    string url = Uri.TryCreate(configuredBase, UriKind.Absolute, out Uri? baseUri)
        ? new Uri(baseUri, context.Request.Path + context.Request.QueryString).ToString()
        : $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
    bool valid = verifier.Provider.Equals("Twilio", StringComparison.OrdinalIgnoreCase)
        && verifier.Verify(url, parameters, signature ?? string.Empty);
    if (!valid) PurpleGlassTelemetry.TelephonyWebhooksInvalid.Add(1, new KeyValuePair<string, object?>("provider", "Twilio"));
    return valid;
}

static bool VerifyTelephonyMediaRequest(
    HttpContext context,
    ITelephonyWebhookVerifier verifier,
    IConfiguration configuration)
{
    string? signature = context.Request.Headers["X-Twilio-Signature"].FirstOrDefault();
    string configuredBase = configuration["Telephony:PublicBaseUrl"] ?? string.Empty;
    string url;
    if (Uri.TryCreate(configuredBase, UriKind.Absolute, out Uri? baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps)
    {
        var mediaUri = new UriBuilder(new Uri(baseUri, context.Request.Path + context.Request.QueryString))
        {
            Scheme = "wss",
            Port = -1,
        };
        url = mediaUri.Uri.ToString();
    }
    else
    {
        PurpleGlassTelemetry.TelephonyWebhooksInvalid.Add(1,
            new KeyValuePair<string, object?>("provider", "Twilio"));
        return false;
    }
    bool valid = verifier.Provider.Equals("Twilio", StringComparison.OrdinalIgnoreCase)
        && verifier.Verify(url, new Dictionary<string, string>(), signature ?? string.Empty);
    if (!valid) PurpleGlassTelemetry.TelephonyWebhooksInvalid.Add(1,
        new KeyValuePair<string, object?>("provider", "Twilio"));
    return valid;
}

static async Task CloseMediaSocketAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
{
    if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
    string safeReason = string.IsNullOrWhiteSpace(reason) ? "media_rejected" : reason[..Math.Min(reason.Length, 80)];
    try
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, safeReason, cancellationToken);
    }
    catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
    {
    }
}

public partial class Program;

namespace PurpleGlass.WebBff
{
    public sealed record DevelopmentLoginRequest(string User);
    public sealed record OutboundTransportRequest(Guid LocationId, string IdempotencyKey, string DestinationNumber,
        string? LanguageCode = null);
    public sealed record DeadLetterListRequest(
        Guid? LocationId, string? MessageType, string? FailureCategory,
        DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, Guid? MessageId,
        Guid? CorrelationId, string? TraceId, int? Page, int? PageSize);

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
                TenancyValidationException validation => (400, "Location setting is invalid", validation.Code),
                CallApplicationException { Code: "call_not_found" } => (404, "Resource not found", "resource_not_found"),
                CallApplicationException { Code: "telephony_route_not_found" or "telephony_location_required" } => (404, "Telephony route not found", "telephony_route_not_found"),
                CallApplicationException { Code: "telephony_number_unavailable" or "provider_identity_unavailable" } => (409, "Telephony is unavailable", exception is CallApplicationException callException ? callException.Code : "telephony_unavailable"),
                CallApplicationException { Code: "telephony_runtime_unavailable" } => (503, "Telephony runtime is unavailable", "telephony_runtime_unavailable"),
                CallApplicationException { Code: "idempotency_conflict" or "call_concurrency_conflict" } => (409, "Call request conflicted", exception is CallApplicationException conflict ? conflict.Code : "call_conflict"),
                CallApplicationException { Code: "unsupported_call_language" } => (400, "Call language is unsupported", "unsupported_call_language"),
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
