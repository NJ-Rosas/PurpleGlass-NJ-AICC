using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Observability;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.Integrations.Worker", builder.Environment.EnvironmentName);
string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddEventingInfrastructure(connectionString);
builder.Services.Configure<OutboxPublisherOptions>(
    builder.Configuration.GetSection(OutboxPublisherOptions.SectionName));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
