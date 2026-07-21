using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PurpleGlass.Eventing.Infrastructure;

public sealed class EventingDbContextFactory : IDesignTimeDbContextFactory<EventingDbContext>
{
    public EventingDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5433;Database=purpleglass;Username=purpleglass;Password=purpleglass_dev_only";
        return new EventingDbContext(new DbContextOptionsBuilder<EventingDbContext>().UseNpgsql(connectionString).Options);
    }
}
