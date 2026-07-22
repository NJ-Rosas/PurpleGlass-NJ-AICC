using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PurpleGlass.CallOrchestrator.Worker;

if (args.Length != 1 || !Enum.TryParse(args[0], true, out SimulatedCallDirection direction))
{
    Console.Error.WriteLine("Usage: dotnet run --project ./samples/PurpleGlass.CallSimulator -- inbound|outbound");
    return 2;
}

var settings = new HostApplicationBuilderSettings
{
    ApplicationName = "PurpleGlass.CallSimulator",
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = Environments.Development,
};
HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);
builder.Logging.ClearProviders();
builder.Services.AddCallOrchestrator(builder.Configuration);
using IHost host = builder.Build();
await host.StartAsync();

using IServiceScope scope = host.Services.CreateScope();
CallOrchestrationService orchestrator = scope.ServiceProvider.GetRequiredService<CallOrchestrationService>();
Guid callSeed = Guid.NewGuid();
SimulatedCallRequest request = CreateRequest(direction, callSeed);
SimulatedCallResult result = await orchestrator.RunAsync(request, CancellationToken.None);

Console.WriteLine($"Direction: {result.Direction}");
Console.WriteLine("State transitions:");
foreach (string transition in result.StateTransitions) Console.WriteLine($"  {transition}");
Console.WriteLine("Transcript:");
foreach (var turn in result.Transcript) Console.WriteLine($"  {turn.Speaker}: {turn.Text}");
Console.WriteLine($"Escalated: {result.Escalated}");
Console.WriteLine($"Summary: {result.Summary?.Summary ?? "Not generated"}");
Console.WriteLine($"Call ID: {result.CallId}");
Console.WriteLine($"Conversation ID: {result.ConversationId}");
Console.WriteLine($"Persisted call state: {result.CallState}");
Console.WriteLine($"Persisted conversation state: {result.ConversationState}");
Console.WriteLine($"Outcome: {result.Outcome}");

await host.StopAsync();
return result.FailureCode is null ? 0 : 1;

static SimulatedCallRequest CreateRequest(SimulatedCallDirection direction, Guid seed)
{
    Guid tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    Guid locationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    SimulatedCallerInput[] inputs = direction == SimulatedCallDirection.Inbound
        ?
        [
            new(GuidFrom(seed, 1), "What are your office hours?"),
            new(GuidFrom(seed, 2), "My name is Taylor Example"),
            new(GuidFrom(seed, 3), "I am calling about an appointment"),
            new(GuidFrom(seed, 4), "No thanks"),
        ]
        :
        [
            new(GuidFrom(seed, 1), "My name is Jordan Example"),
            new(GuidFrom(seed, 2), "I am calling about an appointment"),
            new(GuidFrom(seed, 3), "No thanks"),
        ];
    return new(
        tenantId,
        locationId,
        direction,
        $"sim-{direction.ToString().ToLowerInvariant()}-{seed:N}",
        "+15550100001",
        "+15550100002",
        inputs,
        GuidFrom(seed, 100),
        GuidFrom(seed, 101),
        $"sim-{seed:N}");
}

static Guid GuidFrom(Guid seed, byte marker)
{
    byte[] bytes = seed.ToByteArray();
    bytes[^1] = marker;
    return new Guid(bytes);
}
