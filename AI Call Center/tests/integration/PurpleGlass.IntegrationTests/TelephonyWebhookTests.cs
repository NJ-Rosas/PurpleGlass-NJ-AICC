using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.WebBff;

namespace PurpleGlass.IntegrationTests;

public sealed class TelephonyWebhookTests : IClassFixture<TelephonyWebApplicationFactory>
{
    internal const string AccountSid = "AC11111111111111111111111111111111";
    internal const string AuthToken = "synthetic-webhook-auth-token";
    private const string CallSid = "CA22222222222222222222222222222222";
    private readonly TelephonyWebApplicationFactory factory;

    public TelephonyWebhookTests(TelephonyWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task InvalidSignatureIsRejectedWithoutCreatingCall()
    {
        using IServiceScope beforeScope = factory.Services.CreateScope();
        CallManagementDbContext beforeDb = beforeScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        int before = await beforeDb.Calls.CountAsync(call => call.Provider == "Twilio" && call.ProviderCallId == CallSid);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = InboundRequest("invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        CallManagementDbContext db = scope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        Assert.Equal(before, await db.Calls.CountAsync(call => call.Provider == "Twilio" && call.ProviderCallId == CallSid));
    }

    [Fact]
    public async Task ValidSignedInboundWebhookCreatesOneScopedCallAndDuplicateIsIdempotent()
    {
        Dictionary<string, string> signedParameters = FormValues();
        string signature = Sign("https://example.test/telephony/twilio/inbound", signedParameters);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ITelephonyWebhookVerifier verifier = scope.ServiceProvider.GetRequiredService<ITelephonyWebhookVerifier>();
            Assert.Equal("Twilio", verifier.Provider);
            Assert.True(verifier.Verify("https://example.test/telephony/twilio/inbound", signedParameters, signature));
            CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
            _ = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
                DevelopmentIdentityDirectory.TenantId, DevelopmentIdentityDirectory.LocationId,
                "Twilio", "+17875551222", null, true, false, true), default);
        }

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage first = InboundRequest(signature);
        HttpResponseMessage firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("application/xml", firstResponse.Content.Headers.ContentType?.MediaType);
        string twiml = await firstResponse.Content.ReadAsStringAsync();
        Assert.Contains("<Connect><Stream", twiml, StringComparison.Ordinal);
        Assert.Contains("wss://example.test/telephony/twilio/media", twiml, StringComparison.Ordinal);
        using HttpRequestMessage duplicate = InboundRequest(signature);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(duplicate)).StatusCode);

        using IServiceScope verificationScope = factory.Services.CreateScope();
        CallManagementDbContext db = verificationScope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        Assert.Equal(1, await db.Calls.CountAsync(call => call.Provider == "Twilio" && call.ProviderCallId == CallSid));
        var persisted = await db.Calls.SingleAsync(call => call.Provider == "Twilio" && call.ProviderCallId == CallSid);
        Assert.Equal(DevelopmentIdentityDirectory.TenantId, persisted.TenantId.Value);
        Assert.Equal(DevelopmentIdentityDirectory.LocationId, persisted.LocationId.Value);
    }

    private static HttpRequestMessage InboundRequest(string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/telephony/twilio/inbound")
        {
            Content = new FormUrlEncodedContent(FormValues()),
        };
        request.Headers.Add("X-Twilio-Signature", signature);
        return request;
    }

    private static Dictionary<string, string> FormValues() => new()
    {
        ["AccountSid"] = AccountSid,
        ["CallSid"] = CallSid,
        ["From"] = "+17875550100",
        ["To"] = "+17875551222",
    };

    private static string Sign(string url, IReadOnlyDictionary<string, string> parameters)
    {
        string material = url + string.Concat(parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + pair.Value));
#pragma warning disable CA5350 // Twilio's webhook signature protocol specifically requires HMAC-SHA1.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(AuthToken));
#pragma warning restore CA5350
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(material)));
    }
}

public sealed class TelephonyWebApplicationFactory : WebApplicationFactory<WebBffAssembly>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:AllowDevelopmentAuthentication"] = "true",
                ["Security:AllowSyntheticDataOnly"] = "true",
                ["Security:RequireHttps"] = "false",
                ["Providers:EnableRealTelephony"] = "true",
                ["Telephony:Provider"] = "Twilio",
                ["Telephony:PublicBaseUrl"] = "https://example.test",
                ["Telephony:Twilio:AccountSid"] = TelephonyWebhookTests.AccountSid,
                ["Telephony:Twilio:AuthToken"] = TelephonyWebhookTests.AuthToken,
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITelephonyProvider>();
            services.RemoveAll<ITelephonyWebhookVerifier>();
            services.RemoveAll<TwilioTelephonyOptions>();
            services.AddSingleton(new TwilioTelephonyOptions
            {
                AccountSid = TelephonyWebhookTests.AccountSid,
                AuthToken = TelephonyWebhookTests.AuthToken,
                PublicBaseUrl = "https://example.test",
            });
            services.AddSingleton<ITelephonyProvider, TwilioTelephonyProvider>();
            services.AddSingleton<ITelephonyWebhookVerifier, TwilioWebhookVerifier>();
        });
    }
}
