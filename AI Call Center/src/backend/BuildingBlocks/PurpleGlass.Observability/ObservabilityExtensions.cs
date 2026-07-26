using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PurpleGlass.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public OtlpOptions Otlp { get; set; } = new();
}

public sealed class OtlpOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
}

public sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        if (options.Enabled && string.IsNullOrWhiteSpace(options.ServiceName))
            return ValidateOptionsResult.Fail("Observability:ServiceName is required when observability is enabled.");
        if (options.Otlp.Enabled && (!Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")))
            return ValidateOptionsResult.Fail("Observability:Otlp:Endpoint must be an absolute HTTP(S) URI when OTLP is enabled.");
        return ValidateOptionsResult.Success;
    }
}

public static class ObservabilityExtensions
{
    public static IServiceCollection AddPurpleGlassObservability(this IServiceCollection services, IConfiguration configuration, string defaultServiceName, string environment)
    {
        IConfigurationSection section = configuration.GetSection(ObservabilityOptions.SectionName);
        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
        services.AddOptions<ObservabilityOptions>().Bind(section).PostConfigure(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ServiceName)) o.ServiceName = defaultServiceName;
            if (string.IsNullOrWhiteSpace(o.Environment)) o.Environment = environment;
        }).ValidateOnStart();

        ObservabilityOptions options = section.Get<ObservabilityOptions>() ?? new();
        options.ServiceName = string.IsNullOrWhiteSpace(options.ServiceName) ? defaultServiceName : options.ServiceName;
        options.Environment = string.IsNullOrWhiteSpace(options.Environment) ? environment : options.Environment;
        if (!options.Enabled) return services;

        services.Configure<LoggerFactoryOptions>(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        var telemetry = services.AddOpenTelemetry().ConfigureResource(r => r.AddService(options.ServiceName).AddAttributes([new("deployment.environment.name", options.Environment)]));
        telemetry.WithTracing(t =>
        {
            t.AddSource(PurpleGlassTelemetry.WebBffSourceName, PurpleGlassTelemetry.CallOrchestratorSourceName, PurpleGlassTelemetry.EventingSourceName, PurpleGlassTelemetry.MessagingSourceName, PurpleGlassTelemetry.SecuritySourceName, PurpleGlassTelemetry.IntegrationsSourceName)
             .AddAspNetCoreInstrumentation(o => o.RecordException = true)
             .AddHttpClientInstrumentation(o => o.RecordException = true)
             .AddSource("Npgsql");
            if (options.Otlp.Enabled) t.AddOtlpExporter(o => o.Endpoint = new Uri(options.Otlp.Endpoint!));
        });
        telemetry.WithMetrics(m =>
        {
            m.AddMeter("PurpleGlass").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation();
            if (options.Otlp.Enabled) m.AddOtlpExporter(o => o.Endpoint = new Uri(options.Otlp.Endpoint!));
        });
        return services;
    }
}
