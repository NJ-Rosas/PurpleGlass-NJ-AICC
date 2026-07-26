using Microsoft.Extensions.Configuration;
using Npgsql;

namespace PurpleGlass.Eventing.Infrastructure;

public static class PostgresConfiguration
{
    public static string RequireConnectionString(this IConfiguration configuration)
    {
        string? configured = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
            return configured;

        string[] credentials = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
        if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
            throw new InvalidOperationException("ConnectionStrings:Postgres contains an invalid PostgreSQL URL.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            Username = credentials[0],
            Password = credentials[1],
            SslMode = SslMode.Prefer,
        }.ConnectionString;
    }
}
