using PurpleGlass.Integrations.Worker;
using PurpleGlass.Modules.Tenancy.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddTenancyInfrastructure(connectionString);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
