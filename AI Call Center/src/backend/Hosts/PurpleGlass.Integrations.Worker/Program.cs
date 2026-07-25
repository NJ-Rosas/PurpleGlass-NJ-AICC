using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;

var builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddEventingInfrastructure(connectionString);
builder.Services.Configure<OutboxPublisherOptions>(
    builder.Configuration.GetSection(OutboxPublisherOptions.SectionName));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
