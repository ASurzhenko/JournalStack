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
            // Opening a new connection to a host that does not answer gives up after 5 seconds.
            // This must stay below the admin app's 10 second client timeout, or a database that stops answering
            // shows as 504 in the admin instead of 503. The default is 15.
            Timeout = 5,
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
