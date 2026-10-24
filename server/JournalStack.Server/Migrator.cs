using DbUp;

namespace JournalStack.Server;

public static class Migrator
{
    // Applies the numbered scripts that are not in the schemaversions journal yet.
    // Each script runs in its own transaction, so a failed script leaves nothing half-applied.
    public static int Run(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.EndsWith(".sql"))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            Console.Error.WriteLine($"Migration failed with {result.Error.GetType().Name}.");
            return 1;
        }
        return 0;
    }
}
