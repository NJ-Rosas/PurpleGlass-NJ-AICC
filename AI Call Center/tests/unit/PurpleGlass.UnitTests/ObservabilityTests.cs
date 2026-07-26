using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PurpleGlass.Eventing;
using PurpleGlass.Observability;

namespace PurpleGlass.UnitTests;

public sealed class ObservabilityTests
{
    [Fact]
    public void CorrelationIdIsCreatedWhenMissing() =>
        Assert.NotEqual(Guid.Empty, CorrelationIds.PreserveOrCreate(null));

    [Fact]
    public void ExistingCorrelationIdIsPreserved()
    {
        Guid expected = Guid.NewGuid();
        Assert.Equal(expected, CorrelationIds.PreserveOrCreate(expected));
    }

    [Fact]
    public void ValidW3cContextIsExtracted()
    {
        var envelope = new TraceEnvelope("4bf92f3577b34da6a3ce929d0e0e4736", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "vendor=value");
        Assert.True(envelope.TryExtract(out ActivityContext context));
        Assert.True(context.IsRemote);
        Assert.Equal(envelope.TraceId, context.TraceId.ToHexString());
    }

    [Fact]
    public void InvalidW3cContextIsRejected() =>
        Assert.False(new TraceEnvelope(null, "not-a-trace-parent", null).TryExtract(out _));

    [Fact]
    public void OutboxCapturesCurrentTraceWithoutPayloadChanges()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");
        using Activity? activity = source.StartActivity("create");
        OutboxMessage message = OutboxMessage.Create(Guid.NewGuid(), Guid.NewGuid(), "topic", "type", "{}", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(activity!.TraceId.ToHexString(), message.TraceId);
        Assert.Equal(activity.Id, message.TraceParent);
    }

    [Theory]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(InvalidOperationException), "operation_failed")]
    public void ErrorSanitizationReturnsBoundedCodes(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Equal(expected, TelemetrySanitizer.ErrorCode(exception));
    }

    [Fact]
    public void OtlpDisabledConfigurationBuildsWithoutExporter()
    {
        var values = new Dictionary<string, string?> { ["Observability:Enabled"] = "true", ["Observability:Otlp:Enabled"] = "false" };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPurpleGlassObservability(configuration, "test-service", "Test");
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal("test-service", provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value.ServiceName);
    }

    [Fact]
    public void EnabledOtlpRequiresValidHttpEndpoint()
    {
        var validator = new ObservabilityOptionsValidator();
        ValidateOptionsResult result = validator.Validate(null, new ObservabilityOptions
        {
            ServiceName = "test",
            Otlp = new OtlpOptions { Enabled = true, Endpoint = "secret-value" }
        });
        Assert.True(result.Failed);
    }
}
