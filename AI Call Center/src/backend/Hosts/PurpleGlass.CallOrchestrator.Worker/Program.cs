using PurpleGlass.CallOrchestrator.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCallOrchestrator(builder.Configuration);

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.Run();

public partial class Program;
