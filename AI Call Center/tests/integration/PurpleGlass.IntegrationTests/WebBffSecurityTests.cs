using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.Contains("Permissions-Policy"));
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
}

public sealed class SecurityWebApplicationFactory : WebApplicationFactory<WebBffAssembly>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:AllowDevelopmentAuthentication"] = "true",
                ["Security:AllowSyntheticDataOnly"] = "true",
                ["Security:RequireHttps"] = "false"
            }));
    }
}
