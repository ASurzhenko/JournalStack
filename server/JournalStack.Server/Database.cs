using Npgsql;

namespace JournalStack.Server;

public static class Database
{
    public static string ConnectionString(IConfiguration config)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Required(config, "PGHOST"),
            Port = int.Parse(Required(config, "PGPORT")),
            Username = Required(config, "PGUSER"),
            Password = Required(config, "PGPASSWORD"),
            Database = Required(config, "PGDATABASE"),
            // PostgreSQL puts "Failing row contains (...)" into constraint errors. Entry text must not reach exceptions or logs.
            IncludeErrorDetail = false,
        };
        return builder.ConnectionString;
    }

    private static string Required(IConfiguration config, string name)
    {
        var value = config[name];
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Environment variable {name} is not set.");
        }
        return value;
    }
}
