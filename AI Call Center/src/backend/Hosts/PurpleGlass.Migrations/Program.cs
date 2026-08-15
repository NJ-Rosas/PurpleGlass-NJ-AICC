using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Tenancy.Infrastructure;
using PurpleGlass.Modules.Scheduling.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.RequireConnectionString();

builder.Services.AddTenancyInfrastructure(connectionString);
builder.Services.AddDbContext<EventingDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.AddConversationInfrastructure(connectionString);
builder.Services.AddSchedulingInfrastructure(connectionString);

using IHost host = builder.Build();
await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

await dbContext.Database.MigrateAsync();
await scope.ServiceProvider.GetRequiredService<EventingDbContext>().Database.MigrateAsync();
await scope.ServiceProvider.GetRequiredService<CallManagementDbContext>().Database.MigrateAsync();
await scope.ServiceProvider.GetRequiredService<ConversationDbContext>().Database.MigrateAsync();
await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().Database.MigrateAsync();
await PrototypeSeed.ApplyAsync(dbContext, CancellationToken.None);
