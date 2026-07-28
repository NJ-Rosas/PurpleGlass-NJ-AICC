using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using PurpleGlass.Eventing;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Modules.Tenancy.Infrastructure;
using PurpleGlass.WebBff;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;

namespace PurpleGlass.IntegrationTests;

public sealed class WebBffSecurityTests : IClassFixture<SecurityWebApplicationFactory>
{
    private readonly SecurityWebApplicationFactory factory;

    public WebBffSecurityTests(SecurityWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task ProtectedEndpointRejectsAnonymousBrowserWithStableError()
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/bff/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("authentication_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentLoginCreatesHttpOnlySessionAndSanitizedProjection()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await GetCsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        { Content = JsonContent.Create(new { user = "administrator" }) };
        login.Headers.Add("X-CSRF-TOKEN", csrf);
        HttpResponseMessage loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);
        Assert.Contains(loginResponse.Headers.GetValues("Set-Cookie"), value => value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));

        JsonDocument session = JsonDocument.Parse(await client.GetStringAsync("/bff/v1/session"));
        string json = session.RootElement.GetRawText();
        Assert.Equal("development", session.RootElement.GetProperty("authenticationMethod").GetString());
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CsrfIsRequiredAndValidTokenAllowsProtectedLogin()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        HttpResponseMessage rejected = await client.PostAsJsonAsync("/bff/v1/security/development-login", new { user = "administrator" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("csrf_validation_failed", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        { Content = JsonContent.Create(new { user = "administrator" }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task LogoutClearsSession()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await GetCsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        { Content = JsonContent.Create(new { user = "administrator" }) };
        login.Headers.Add("X-CSRF-TOKEN", csrf);
        _ = await client.SendAsync(login);

        csrf = await GetCsrfAsync(client);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/logout");
        logout.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/bff/v1/session")).StatusCode);
    }

    [Fact]
    public async Task SecurityHeadersAreCentralized()
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.Contains("Permissions-Policy"));
    }

    [Fact]
    public async Task RenderLivenessDoesNotBypassOperationalReadiness()
    {
        await using var unavailableDependencyFactory = new SecurityWebApplicationFactory(includeUnhealthyReadinessCheck: true);
        using HttpClient client = unavailableDependencyFactory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using JsonDocument liveBody = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
        using JsonDocument readyBody = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", liveBody.RootElement.GetProperty("status").GetString());
        Assert.Empty(liveBody.RootElement.GetProperty("checks").EnumerateObject());
        Assert.Equal("Unhealthy", readyBody.RootElement.GetProperty("status").GetString());
        Assert.Contains(readyBody.RootElement.GetProperty("checks").EnumerateObject(),
            check => check.Value.GetProperty("status").GetString() == "Unhealthy");
    }

    [Fact]
    public async Task RenderHostCanReachLivenessEndpoint()
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "purpleglass-web.onrender.com";
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "purpleglass-web.onrender.com");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("administrator", "TenantAdministrator", "tenant.settings.manage", true)]
    [InlineData("read-only", "ReadOnlyUser", "tenant.settings.manage", false)]
    public async Task RenderOriginCanCompleteSecureDevelopmentLogin(
        string user,
        string expectedRole,
        string administrativePermission,
        bool permissionExpected)
    {
        await using var renderFactory = new SecurityWebApplicationFactory(renderProxy: true);
        using HttpClient client = renderFactory.CreateClient();
        client.BaseAddress = new Uri("https://purpleglass-web.onrender.com");

        (HttpResponseMessage login, HttpResponseMessage session) = await LoginFromRenderAsync(client, user);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        string sessionCookie = login.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("PurpleGlass.Dev.Session=", StringComparison.Ordinal));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using JsonDocument projection = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Assert.Equal(expectedRole, projection.RootElement.GetProperty("role").GetString());
        bool hasPermission = projection.RootElement.GetProperty("permissions").EnumerateArray()
            .Any(permission => permission.GetString() == administrativePermission);
        Assert.Equal(permissionExpected, hasPermission);
    }

    [Fact]
    public async Task UntrustedBrowserOriginIsNotGrantedCorsAccess()
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/bff/v1/security/development-login");
        request.Headers.Add("Origin", "https://untrusted.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ReadOnlyUserCannotInitiateOutboundCall()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "read-only");
        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/calls/outbound")
        { Content = JsonContent.Create(new { idempotencyKey = "security-test", fromNumber = "+15550000001", toNumber = "+15550000002" }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AuthorizedUserCanReadCallsButUnknownCallIsNotDisclosed()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/bff/v1/calls")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/bff/v1/calls/{Guid.NewGuid():D}")).StatusCode);
    }

    [Fact]
    public async Task UnauthorizedLocationMutationIsHidden()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/v1/locations/{Guid.NewGuid():D}/display-name")
        { Content = JsonContent.Create(new { displayName = "Synthetic", expectedVersion = 1 }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AnonymousSseConnectionIsRejected()
    {
        using HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/bff/v1/events")).StatusCode);
    }

    [Fact]
    public async Task AdministratorOutboundRequestUsesCsrfScopeAuditAndDurableIntent()
    {
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            _ = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
                DevelopmentIdentityDirectory.TenantId, DevelopmentIdentityDirectory.LocationId,
                "None", "+17875551300", null, false, true, true), default);
        }
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        string csrf = await GetCsrfAsync(client);
        string idempotencyKey = $"bff-transport-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/calls/outbound")
        {
            Content = JsonContent.Create(new
            {
                locationId = DevelopmentIdentityDirectory.LocationId,
                idempotencyKey,
                destinationNumber = "+17875551301",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using IServiceScope verificationScope = factory.Services.CreateScope();
        CallManagementDbContext callsDb = verificationScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        Assert.True(await callsDb.TelephonyOperations.AnyAsync(operation => operation.TenantId
            == new PurpleGlass.Modules.CallManagement.Domain.TenantId(DevelopmentIdentityDirectory.TenantId)));
        TenancyDbContext tenancy = verificationScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        Assert.True(await tenancy.AuditRecords.AnyAsync(record => record.Action == "OutboundCallRequested"));
    }

    [Fact]
    public async Task UnavailableWorkerRejectsOutboundBeforeDurableIntent()
    {
        await using var unavailableWorkerFactory = new SecurityWebApplicationFactory(
            renderProxy: false, workerReady: false);
        int operationsBefore;
        using (IServiceScope beforeScope = unavailableWorkerFactory.Services.CreateScope())
        {
            CallManagementDbContext beforeCalls = beforeScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
            operationsBefore = await beforeCalls.TelephonyOperations.CountAsync();
        }
        using HttpClient client = unavailableWorkerFactory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/calls/outbound")
        {
            Content = JsonContent.Create(new
            {
                locationId = DevelopmentIdentityDirectory.LocationId,
                idempotencyKey = $"unavailable-{Guid.NewGuid():N}",
                destinationNumber = "+17875551301",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using IServiceScope afterScope = unavailableWorkerFactory.Services.CreateScope();
        CallManagementDbContext afterCalls = afterScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        Assert.Equal(operationsBefore, await afterCalls.TelephonyOperations.CountAsync());
    }

    [Fact]
    public async Task AdministratorHangupCreatesOneDurableOperation()
    {
        Guid callId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            var call = await calls.RegisterInboundAsync(new RegisterInboundCall(
                DevelopmentIdentityDirectory.TenantId, DevelopmentIdentityDirectory.LocationId,
                $"FA-{Guid.NewGuid():N}", "+17875551301", "+17875551300", Guid.NewGuid(), Provider: "Fake"), default);
            callId = call.CallId;
        }
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        string csrf = await GetCsrfAsync(client);
        using var first = new HttpRequestMessage(HttpMethod.Post, $"/bff/v1/calls/{callId:D}/hangup");
        first.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(first)).StatusCode);
        csrf = await GetCsrfAsync(client);
        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/bff/v1/calls/{callId:D}/hangup");
        replay.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(replay)).StatusCode);
        using IServiceScope verificationScope = factory.Services.CreateScope();
        CallManagementDbContext db = verificationScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        Assert.Equal(1, await db.TelephonyOperations.CountAsync(operation => operation.CallId
            == new PurpleGlass.Modules.CallManagement.Domain.CallSessionId(callId)
            && operation.Type == PurpleGlass.Modules.CallManagement.Domain.TelephonyOperationType.Hangup));
    }

    [Fact]
    public async Task ReadOnlyUserCannotListOrRecoverDeadLetters()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "read-only");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/bff/v1/operations/dead-letters")).StatusCode);
        string csrf = await GetCsrfAsync(client);
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/bff/v1/operations/dead-letters/{Guid.NewGuid():D}/retry");
        retry.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(retry)).StatusCode);
    }

    [Fact]
    public async Task AdministratorCanInspectAndRecoverWithoutPayloadDisclosure()
    {
        OutboxMessage message = await SeedDeadLetterAsync(DevelopmentIdentityDirectory.TenantId, DevelopmentIdentityDirectory.LocationId);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");

        string listJson = await client.GetStringAsync("/bff/v1/operations/dead-letters?page=1&pageSize=10");
        Assert.Contains(message.Id.ToString("D"), listJson, StringComparison.OrdinalIgnoreCase);
        string detailJson = await client.GetStringAsync($"/bff/v1/operations/dead-letters/{message.Id:D}");
        Assert.DoesNotContain("private-payload", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"payload\"", detailJson, StringComparison.OrdinalIgnoreCase);

        string csrf = await GetCsrfAsync(client);
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/bff/v1/operations/dead-letters/{message.Id:D}/retry");
        retry.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(retry)).StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        EventingDbContext eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        OutboxMessage recovered = await eventing.OutboxMessages.AsNoTracking().SingleAsync(candidate => candidate.Id == message.Id);
        Assert.Equal(OutboxMessage.PendingStatus, recovered.Status);
        Assert.Equal(1, recovered.RecoveryCount);
        TenancyDbContext tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        Assert.True(await tenancy.AuditRecords.AnyAsync(record => record.Action == "DeadLetterRequeued" && record.ResourceId == message.Id.ToString("D")));
    }

    [Fact]
    public async Task CrossTenantDeadLetterLookupDoesNotDiscloseExistence()
    {
        OutboxMessage message = await SeedDeadLetterAsync(Guid.NewGuid(), Guid.NewGuid());
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "administrator");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/bff/v1/operations/dead-letters/{message.Id:D}")).StatusCode);
    }

    private async Task<OutboxMessage> SeedDeadLetterAsync(Guid tenantId, Guid locationId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        EventingDbContext db = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-2);
        OutboxMessage message = OutboxMessage.Create(tenantId, locationId, "topic", "SyntheticDeadLetter",
            "{\"private-payload\":\"must-not-escape\"}", Guid.NewGuid(), now);
        Guid lease = Guid.NewGuid();
        message.Claim(lease, now.AddMinutes(1));
        message.MarkFailed(lease, "timeout", now.AddMinutes(1), 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        db.Add(message);
        _ = await db.SaveChangesAsync();
        return message;
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/bff/v1/security/csrf"));
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        string csrf = await GetCsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        { Content = JsonContent.Create(new { user }) };
        login.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(login)).StatusCode);
    }

    private static async Task<(HttpResponseMessage Login, HttpResponseMessage Session)> LoginFromRenderAsync(
        HttpClient client,
        string user)
    {
        const string origin = "https://purpleglass-web.onrender.com";
        client.DefaultRequestHeaders.Host = "purpleglass-web.onrender.com";
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "purpleglass-web.onrender.com");
        client.DefaultRequestHeaders.Add("Origin", origin);
        client.DefaultRequestHeaders.Add("Referer", $"{origin}/");

        HttpResponseMessage csrf = await client.GetAsync("/bff/v1/security/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        string csrfCookie = csrf.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("PurpleGlass.Dev.Csrf=", StringComparison.Ordinal));
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", csrfCookie, StringComparison.OrdinalIgnoreCase);
        string csrfCookiePair = csrfCookie[..csrfCookie.IndexOf(';')];
        using JsonDocument token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        { Content = JsonContent.Create(new { user }) };
        loginRequest.Headers.Add("X-CSRF-TOKEN", token.RootElement.GetProperty("token").GetString());
        loginRequest.Headers.Add("Cookie", csrfCookiePair);
        HttpResponseMessage login = await client.SendAsync(loginRequest);
        string sessionCookie = login.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("PurpleGlass.Dev.Session=", StringComparison.Ordinal));
        string sessionCookiePair = sessionCookie[..sessionCookie.IndexOf(';')];

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/bff/v1/session");
        sessionRequest.Headers.Add("Cookie", sessionCookiePair);
        HttpResponseMessage session = await client.SendAsync(sessionRequest);
        return (login, session);
    }
}

public sealed class SecurityWebApplicationFactory : WebApplicationFactory<WebBffAssembly>
{
    private readonly bool includeUnhealthyReadinessCheck;
    private readonly bool renderProxy;
    private readonly bool workerReady = true;

    public SecurityWebApplicationFactory()
    {
    }

    internal SecurityWebApplicationFactory(bool includeUnhealthyReadinessCheck) =>
        this.includeUnhealthyReadinessCheck = includeUnhealthyReadinessCheck;

    internal SecurityWebApplicationFactory(
        bool renderProxy, bool includeUnhealthyReadinessCheck = false, bool workerReady = true)
    {
        this.renderProxy = renderProxy;
        this.includeUnhealthyReadinessCheck = includeUnhealthyReadinessCheck;
        this.workerReady = workerReady;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (renderProxy)
        {
            builder.UseSetting("forwardedHeaders", "true");
            builder.UseSetting("Security:ForceSecureCookies", "true");
        }
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:AllowDevelopmentAuthentication"] = "true",
                ["Security:AllowSyntheticDataOnly"] = "true",
                ["Security:RequireHttps"] = "false",
                ["Security:ForceSecureCookies"] = renderProxy ? "true" : "false",
                ["AllowedHosts"] = "localhost;127.0.0.1;purpleglass-web.onrender.com",
                ["WorkerRuntime:ReadyUrl"] = "https://worker.test/health/ready",
                ["WorkerRuntime:ReadyTimeoutSeconds"] = "5",
                ["WorkerRuntime:PollMilliseconds"] = "250",
            }));
        builder.ConfigureServices(services => services
            .AddHttpClient(nameof(WorkerRuntimeGateway))
            .ConfigurePrimaryHttpMessageHandler(() => new WorkerReadinessHandler(workerReady)));
        if (includeUnhealthyReadinessCheck)
        {
            builder.ConfigureServices(services => services.AddHealthChecks().AddCheck(
                "unavailable-dependency",
                () => HealthCheckResult.Unhealthy("Synthetic dependency failure."),
                tags: ["ready"]));
        }
    }

    private sealed class WorkerReadinessHandler(bool ready) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(ready
                ? HttpStatusCode.OK
                : HttpStatusCode.ServiceUnavailable));
    }
}
