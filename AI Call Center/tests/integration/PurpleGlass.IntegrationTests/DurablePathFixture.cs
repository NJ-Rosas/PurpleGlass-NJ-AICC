using Microsoft.EntityFrameworkCore;
using Npgsql;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Tenancy.Infrastructure;

namespace PurpleGlass.IntegrationTests;

public class DurablePathFixture : IAsyncLifetime
{
    private readonly string databaseName = $"purpleglass_task3_{Guid.NewGuid():N}";
    private const string AdminConnection = "Host=localhost;Port=5433;Database=postgres;Username=purpleglass;Password=purpleglass_dev_only";

    public string ConnectionString => $"Host=localhost;Port=5433;Database={databaseName};Username=purpleglass;Password=purpleglass_dev_only";

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(AdminConnection))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            _ = await command.ExecuteNonQueryAsync();
        }

        await using TenancyDbContext tenancy = CreateTenancy();
        await tenancy.Database.MigrateAsync();
        await using EventingDbContext eventing = CreateEventing();
        await eventing.Database.MigrateAsync();
        await using CallManagementDbContext calls = CreateCalls();
        await calls.Database.MigrateAsync();
        await using ConversationDbContext conversations = CreateConversations();
        await conversations.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        _ = await command.ExecuteNonQueryAsync();
    }

    public TenancyDbContext CreateTenancy() => new(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(ConnectionString).Options);
    public EventingDbContext CreateEventing() => new(new DbContextOptionsBuilder<EventingDbContext>().UseNpgsql(ConnectionString).Options);
    public CallManagementDbContext CreateCalls() => new(new DbContextOptionsBuilder<CallManagementDbContext>().UseNpgsql(ConnectionString).Options);
    public ConversationDbContext CreateConversations() => new(new DbContextOptionsBuilder<ConversationDbContext>().UseNpgsql(ConnectionString).Options);
}

[CollectionDefinition(Name)]
public sealed class DurablePathGroup : ICollectionFixture<DurablePathFixture>
{
    public const string Name = "durable-path";
}
