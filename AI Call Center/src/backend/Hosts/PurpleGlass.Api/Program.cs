using PurpleGlass.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.Api", builder.Environment.EnvironmentName);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program;
