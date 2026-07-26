using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PurpleGlass.WebBff;

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
