using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Observability;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
if (int.TryParse(builder.Configuration["PORT"], out int renderPort) && renderPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.Integrations.Worker", builder.Environment.EnvironmentName);
string connectionString = builder.Configuration.RequireConnectionString();

builder.Services.AddEventingInfrastructure(connectionString);
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.Configure<OutboxPublisherOptions>(
    builder.Configuration.GetSection(OutboxPublisherOptions.SectionName));
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<WorkerRuntimeState>();
builder.Services.Configure<TelephonyRuntimeOptions>(builder.Configuration.GetSection(TelephonyRuntimeOptions.SectionName));
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
}
else if (builder.Environment.IsDevelopment() && telephonyProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<FakeTelephonyProvider>();
    builder.Services.AddSingleton<ITelephonyProvider>(service => service.GetRequiredService<FakeTelephonyProvider>());
}
else
{
    builder.Services.AddSingleton<ITelephonyProvider, DisabledTelephonyProvider>();
}
builder.Services.AddHostedService<TelephonyTransportWorker>();
builder.Services.AddSingleton<TelephonyDispatchProcessor>();
builder.Services.AddHealthChecks()
    .AddCheck<WorkerDatabaseHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<WorkerMqttHealthCheck>("mqtt", tags: ["ready"])
    .AddCheck<WorkerProcessingHealthCheck>("worker", tags: ["ready"])
    .AddCheck<WorkerTelephonyHealthCheck>("telephony", tags: ["ready"]);

var app = builder.Build();
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
app.Run();

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

public partial class Program;
