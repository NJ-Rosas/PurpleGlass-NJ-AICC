using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PurpleGlass.Modules.Tenancy.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5433;Database=purpleglass;Username=purpleglass;Password=purpleglass_dev_only";

builder.Services.AddTenancyInfrastructure(connectionString);

using IHost host = builder.Build();
await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

await dbContext.Database.MigrateAsync();
await PrototypeSeed.ApplyAsync(dbContext, CancellationToken.None);
