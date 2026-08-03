using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Eventing;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Infrastructure;
using PurpleGlass.WebBff;

namespace PurpleGlass.IntegrationTests;

[Collection(DurablePathGroup.Name)]
public sealed class OutboundCallCreationApiTests(DurablePathFixture fixture)
{
    private const string SourceNumber = "+17875551400";
    private const string DestinationNumber = "+17875551401";

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("es-US", "es-US")]
    [InlineData("es-PR", "es-PR")]
    public async Task SupportedOverrideUsesProductionGraphAndPersistsOneDispatch(
        string requestedLanguage, string normalizedLanguage)
    {
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        string key = $"supported-{Guid.NewGuid():N}";
        (int callsBefore, int operationsBefore, int outboxBefore) = await CountsAsync(factory);

        HttpResponseMessage response = await SendOutboundAsync(
            client, key, requestedLanguage, DestinationNumber);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid callId = body.RootElement.GetProperty("callId").GetGuid();
        Assert.Equal(normalizedLanguage, body.RootElement.GetProperty("startingLanguageCode").GetString());
        Assert.Equal("call_override", body.RootElement.GetProperty("startingLanguageReason").GetString());
        await AssertDurableCreationAsync(factory, callId, normalizedLanguage, "call_override",
            callsBefore, operationsBefore, outboxBefore);

        HttpResponseMessage replay = await SendOutboundAsync(
            client, key, requestedLanguage, DestinationNumber);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        using JsonDocument replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(callId, replayBody.RootElement.GetProperty("callId").GetGuid());
        await AssertDurableCreationAsync(factory, callId, normalizedLanguage, "call_override",
            callsBefore, operationsBefore, outboxBefore);
        Assert.Empty(factory.Services.GetRequiredService<FakeTelephonyProvider>().Calls);
    }

    [Fact]
    public async Task WorkerDispatchesCreatedCallOnceAndPreservesResolvedLanguage()
    {
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        FakeTelephonyProvider provider = factory.Services.GetRequiredService<FakeTelephonyProvider>();
        var processor = CreateProcessor(factory, provider);
        for (int index = 0; index < 100 && await processor.ProcessNextAsync(default); index++) { }
        int providerCallsBefore = provider.Calls.Count;
        string key = $"worker-{Guid.NewGuid():N}";
        HttpResponseMessage response = await SendOutboundAsync(
            client, key, "es-PR", DestinationNumber);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid callId = body.RootElement.GetProperty("callId").GetGuid();

        Assert.True(await processor.ProcessNextAsync(default));

        Assert.Equal(providerCallsBefore + 1, provider.Calls.Count);
        OutboundCallTransport providerRequest = Assert.Single(
            provider.Requests.Values, request => request.CallId == callId);
        Assert.Equal("es-PR", providerRequest.StartingLanguageCode);
        Assert.Equal("call_override", providerRequest.StartingLanguageReason);
        using IServiceScope scope = factory.Services.CreateScope();
        CallManagementDbContext db = scope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        CallSession persisted = await db.Calls.AsNoTracking()
            .SingleAsync(call => call.Id == new CallSessionId(callId));
        Assert.Equal("es-PR", persisted.StartingLanguageCode);
        Assert.Equal("call_override", persisted.StartingLanguageReason);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ProviderCallId));
        Assert.Equal(1, await db.TelephonyOperations.CountAsync(
            operation => operation.CallId == new CallSessionId(callId)));

        Assert.Equal(HttpStatusCode.Accepted,
            (await SendOutboundAsync(client, key, "es-PR", DestinationNumber)).StatusCode);
        Assert.False(await processor.ProcessNextAsync(default));
        Assert.Equal(providerCallsBefore + 1, provider.Calls.Count);
    }

    [Fact]
    public async Task OmittedOverrideUsesLocationDefaultAndMatchingOverrideRemainsExplicit()
    {
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        using IServiceScope tenancyScope = factory.Services.CreateScope();
        TenancyDbContext tenancy = tenancyScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        long originalVersion = await tenancy.Locations
            .Where(location => location.Id == new PurpleGlass.Modules.Tenancy.Domain.LocationId(
                DevelopmentIdentityDirectory.LocationId))
            .Select(location => location.Version).SingleAsync();
        string csrf = await GetCsrfAsync(client);
        using var update = new HttpRequestMessage(HttpMethod.Put,
            $"/bff/v1/locations/{DevelopmentIdentityDirectory.LocationId:D}/default-call-language")
        {
            Content = JsonContent.Create(new { languageCode = "es-US", expectedVersion = originalVersion }),
        };
        update.Headers.Add("X-CSRF-TOKEN", csrf);
        HttpResponseMessage updated = await client.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        HttpResponseMessage locationDefault = await SendOutboundAsync(
            client, $"default-{Guid.NewGuid():N}", null, DestinationNumber);
        Assert.Equal(HttpStatusCode.Accepted, locationDefault.StatusCode);
        using JsonDocument defaultBody = JsonDocument.Parse(await locationDefault.Content.ReadAsStringAsync());
        Assert.Equal("es-US", defaultBody.RootElement.GetProperty("startingLanguageCode").GetString());
        Assert.Equal("location_default", defaultBody.RootElement.GetProperty("startingLanguageReason").GetString());

        HttpResponseMessage matchingOverride = await SendOutboundAsync(
            client, $"matching-{Guid.NewGuid():N}", "es-US", DestinationNumber);
        Assert.Equal(HttpStatusCode.Accepted, matchingOverride.StatusCode);
        using JsonDocument overrideBody = JsonDocument.Parse(await matchingOverride.Content.ReadAsStringAsync());
        Assert.Equal("es-US", overrideBody.RootElement.GetProperty("startingLanguageCode").GetString());
        Assert.Equal("call_override", overrideBody.RootElement.GetProperty("startingLanguageReason").GetString());

        using JsonDocument updateBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        long changedVersion = updateBody.RootElement.GetProperty("version").GetInt64();
        csrf = await GetCsrfAsync(client);
        using var restore = new HttpRequestMessage(HttpMethod.Put,
            $"/bff/v1/locations/{DevelopmentIdentityDirectory.LocationId:D}/default-call-language")
        {
            Content = JsonContent.Create(new { languageCode = "en-US", expectedVersion = changedVersion }),
        };
        restore.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(restore)).StatusCode);
    }

    [Fact]
    public async Task UnsupportedInvalidLocationAndReadOnlyRequestsCreateNothing()
    {
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString);
        using HttpClient administrator = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        (int callsBefore, int operationsBefore, int outboxBefore) = await CountsAsync(factory);

        HttpResponseMessage unsupported = await SendOutboundAsync(
            administrator, $"unsupported-{Guid.NewGuid():N}", "fr-FR", DestinationNumber);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Contains("unsupported_call_language", await unsupported.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage invalidLocation = await SendOutboundAsync(
            administrator, $"location-{Guid.NewGuid():N}", "en-US", DestinationNumber, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, invalidLocation.StatusCode);

        using HttpClient reader = await CreateClientAsync(factory, "read-only");
        HttpResponseMessage readOnly = await SendOutboundAsync(
            reader, $"reader-{Guid.NewGuid():N}", "en-US", DestinationNumber);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);

        Assert.Equal((callsBefore, operationsBefore, outboxBefore), await CountsAsync(factory));
    }

    [Fact]
    public async Task ConflictingIdempotentRetryReturnsBoundedConflictWithoutSecondCall()
    {
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        string key = $"conflict-{Guid.NewGuid():N}";
        (int callsBefore, int operationsBefore, int outboxBefore) = await CountsAsync(factory);
        Assert.Equal(HttpStatusCode.Accepted,
            (await SendOutboundAsync(client, key, "en-US", DestinationNumber)).StatusCode);

        HttpResponseMessage conflict = await SendOutboundAsync(client, key, "es-US", DestinationNumber);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        (int callsAfter, int operationsAfter, int outboxAfter) = await CountsAsync(factory);
        Assert.Equal(callsBefore + 1, callsAfter);
        Assert.Equal(operationsBefore + 1, operationsAfter);
        Assert.Equal(outboxBefore + 1, outboxAfter);
    }

    [Fact]
    public async Task MalformedDestinationArrayReturnsBoundedErrorAndSafeDiagnostic()
    {
        var logs = new RecordingLoggerProvider();
        await using var factory = new ProductionOutboundWebApplicationFactory(fixture.ConnectionString, logs);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        (int callsBefore, int operationsBefore, int outboxBefore) = await CountsAsync(factory);
        const string idempotencyCanary = "do-not-log-idempotency-canary";
        const string phoneCanary = "+17875551999";
        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/calls/outbound")
        {
            Content = JsonContent.Create(new
            {
                locationId = DevelopmentIdentityDirectory.LocationId,
                idempotencyKey = idempotencyCanary,
                destinationNumber = new[] { phoneCanary },
                languageCode = "en-US",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_request", responseBody, StringComparison.Ordinal);
        Assert.Contains("traceId", responseBody, StringComparison.Ordinal);
        LogEntry diagnostic = Assert.Single(logs.Entries, entry => entry.EventId == 222);
        Assert.Contains("Stage=model_binding", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("SafeCategory=invalid_request", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("ExceptionType=BadHttpRequestException", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyCanary, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(phoneCanary, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("destinationNumber", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal((callsBefore, operationsBefore, outboxBefore), await CountsAsync(factory));
    }

    [Fact]
    public async Task UnexpectedCallCreationFailureLogsOnlyBoundedStageAndType()
    {
        var logs = new RecordingLoggerProvider();
        await using var factory = new ProductionOutboundWebApplicationFactory(
            fixture.ConnectionString, logs, failLanguageResolution: true);
        using HttpClient client = await CreateAdministratorClientAsync(factory);
        await ConfigureNumberAsync(factory);
        (int callsBefore, int operationsBefore, int outboxBefore) = await CountsAsync(factory);

        HttpResponseMessage response = await SendOutboundAsync(
            client, "do-not-log-failure-key", null, "+17875551888");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unexpected_error", body, StringComparison.Ordinal);
        Assert.Contains("traceId", body, StringComparison.Ordinal);
        LogEntry diagnostic = Assert.Single(logs.Entries, entry => entry.EventId == 221);
        Assert.Contains("SafeCategory=unexpected_error", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Stage=language_validated", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("ExceptionType=InvalidOperationException", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("resolver-secret-canary", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-failure-key", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("+17875551888", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal((callsBefore, operationsBefore, outboxBefore), await CountsAsync(factory));
    }

    private static async Task AssertDurableCreationAsync(
        ProductionOutboundWebApplicationFactory factory,
        Guid callId, string language, string reason,
        int callsBefore, int operationsBefore, int outboxBefore)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CallManagementDbContext db = scope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        CallSession call = await db.Calls.AsNoTracking()
            .SingleAsync(item => item.Id == new CallSessionId(callId));
        Assert.Equal(language, call.StartingLanguageCode);
        Assert.Equal(reason, call.StartingLanguageReason);
        Assert.Equal(callsBefore + 1, await db.Calls.CountAsync());
        Assert.Equal(operationsBefore + 1, await db.TelephonyOperations.CountAsync());
        Assert.Equal(1, await db.TelephonyOperations.CountAsync(
            operation => operation.CallId == new CallSessionId(callId)));
        Assert.Equal(outboxBefore + 1, await db.OutboxMessages.CountAsync());
        List<OutboundCallRequested> payloads = (await db.OutboxMessages.AsNoTracking()
                .Where(message => message.MessageType == nameof(OutboundCallRequested))
                .Select(message => message.Payload)
                .ToListAsync())
            .Select(payload => JsonSerializer.Deserialize<OutboundCallRequested>(payload))
            .OfType<OutboundCallRequested>()
            .Where(payload => payload.CallId == callId)
            .ToList();
        Assert.Single(payloads);
    }

    private static async Task<(int Calls, int Operations, int Outbox)> CountsAsync(
        ProductionOutboundWebApplicationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CallManagementDbContext db = scope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
        return (await db.Calls.CountAsync(), await db.TelephonyOperations.CountAsync(),
            await db.OutboxMessages.CountAsync());
    }

    private static async Task ConfigureNumberAsync(ProductionOutboundWebApplicationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CallManagementService calls = scope.ServiceProvider.GetRequiredService<CallManagementService>();
        IReadOnlyList<PurpleGlass.Modules.CallManagement.Contracts.TelephonyNumberSummary> configured =
            await calls.GetTelephonyNumbersAsync(DevelopmentIdentityDirectory.TenantId, default);
        foreach (PurpleGlass.Modules.CallManagement.Contracts.TelephonyNumberSummary existing in configured.Where(
                     number => number.LocationId == DevelopmentIdentityDirectory.LocationId
                         && number.Active
                         && (!string.Equals(number.Provider, "Fake", StringComparison.Ordinal)
                             || !string.Equals(number.Number, SourceNumber, StringComparison.Ordinal))))
        {
            _ = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
                existing.TenantId, existing.LocationId, existing.Provider, existing.Number,
                null, existing.InboundEnabled, existing.OutboundEnabled, false), default);
        }
        _ = await calls.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
            DevelopmentIdentityDirectory.TenantId, DevelopmentIdentityDirectory.LocationId,
            "Fake", SourceNumber, null, true, true, true), default);
    }

    private static TelephonyDispatchProcessor CreateProcessor(
        ProductionOutboundWebApplicationFactory factory, FakeTelephonyProvider provider) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), provider,
            Options.Create(new TelephonyRuntimeOptions
            {
                PublicBaseUrl = "https://example.test",
                MaximumQueueAgeSeconds = 120,
            }),
            TimeProvider.System, NullLogger<TelephonyDispatchProcessor>.Instance);

    private static async Task<HttpClient> CreateAdministratorClientAsync(
        ProductionOutboundWebApplicationFactory factory) =>
        await CreateClientAsync(factory, "administrator");

    private static async Task<HttpClient> CreateClientAsync(
        ProductionOutboundWebApplicationFactory factory, string user)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        factory.AssertUsesConfiguredPostgres();
        string csrf = await GetCsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/security/development-login")
        {
            Content = JsonContent.Create(new { user }),
        };
        login.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(login)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/bff/v1/session")).StatusCode);
        return client;
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/bff/v1/security/csrf"));
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendOutboundAsync(
        HttpClient client, string key, string? language, string destination, Guid? locationId = null)
    {
        string csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/calls/outbound")
        {
            Content = JsonContent.Create(new
            {
                locationId = locationId ?? DevelopmentIdentityDirectory.LocationId,
                idempotencyKey = key,
                destinationNumber = destination,
                languageCode = language,
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private sealed class ProductionOutboundWebApplicationFactory(
        string connectionString,
        RecordingLoggerProvider? logs = null,
        bool failLanguageResolution = false) : WebApplicationFactory<WebBffAssembly>
    {
        public void AssertUsesConfiguredPostgres()
        {
            using IServiceScope scope = Services.CreateScope();
            CallManagementDbContext db = scope.ServiceProvider.GetRequiredService<CallManagementDbContext>();
            Assert.Equal(connectionString, db.Database.GetConnectionString());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Security:AllowDevelopmentAuthentication"] = "true",
                    ["Security:AllowSyntheticDataOnly"] = "true",
                    ["Security:RequireHttps"] = "false",
                    ["Security:ForceSecureCookies"] = "false",
                    ["WorkerRuntime:ReadyUrl"] = "https://worker.test/health/ready",
                    ["WorkerRuntime:ReadyTimeoutSeconds"] = "5",
                    ["WorkerRuntime:PollMilliseconds"] = "250",
                    ["Telephony:Provider"] = "Fake",
                    ["Providers:EnableRealTelephony"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                ReplaceDatabase<TenancyDbContext>(services, connectionString);
                ReplaceDatabase<CallManagementDbContext>(services, connectionString);
                ReplaceDatabase<ConversationDbContext>(services, connectionString);
                ReplaceDatabase<EventingDbContext>(services, connectionString);
                services.RemoveAll<ITelephonyProvider>();
                services.RemoveAll<ITelephonyWebhookVerifier>();
                services.AddSingleton<FakeTelephonyProvider>();
                services.AddSingleton<ITelephonyProvider>(provider =>
                    provider.GetRequiredService<FakeTelephonyProvider>());
                services.AddSingleton<ITelephonyWebhookVerifier>(provider =>
                    provider.GetRequiredService<FakeTelephonyProvider>());
                services.AddHttpClient(nameof(WorkerRuntimeGateway))
                    .ConfigurePrimaryHttpMessageHandler(() => new ReadyHandler());
                if (logs is not null) services.AddLogging(logging => logging.AddProvider(logs));
                if (failLanguageResolution)
                {
                    services.RemoveAll<ILocationCallLanguageResolver>();
                    services.AddSingleton<ILocationCallLanguageResolver, FailingLanguageResolver>();
                }
            });
        }

        private static void ReplaceDatabase<TContext>(
            IServiceCollection services, string configuredConnectionString)
            where TContext : DbContext
        {
            services.RemoveAll<DbContextOptions<TContext>>();
            services.AddDbContext<TContext>(options => options.UseNpgsql(configuredConnectionString));
        }
    }

    private sealed class ReadyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class FailingLanguageResolver : ILocationCallLanguageResolver
    {
        public Task<string?> ResolveDefaultLanguageAsync(
            Guid tenantId, Guid locationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("resolver-secret-canary");
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);
        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(eventId.Id, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(int EventId, string Message);
}
