using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PurpleGlass.Modules.Conversation.Infrastructure;

public sealed class ConversationDbContextFactory : IDesignTimeDbContextFactory<ConversationDbContext>
{
    public ConversationDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5433;Database=purpleglass;Username=purpleglass;Password=purpleglass_dev_only";
        return new ConversationDbContext(new DbContextOptionsBuilder<ConversationDbContext>().UseNpgsql(connectionString).Options);
    }
}
