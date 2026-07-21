using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PurpleGlass.Modules.CallManagement.Infrastructure;

public sealed class CallManagementDbContextFactory : IDesignTimeDbContextFactory<CallManagementDbContext>
{
    public CallManagementDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5433;Database=purpleglass;Username=purpleglass;Password=purpleglass_dev_only";
        var options = new DbContextOptionsBuilder<CallManagementDbContext>().UseNpgsql(connectionString).Options;
        return new CallManagementDbContext(options);
    }
}
