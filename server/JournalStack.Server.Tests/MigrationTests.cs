using Dapper;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class MigrationTests
{
    private readonly PostgresFixture _db;

    public MigrationTests(PostgresFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task Migrate_creates_the_three_tables_and_a_second_run_changes_nothing()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        var connectionString = Database.ConnectionString(PostgresFixture.Config(settings));

        Assert.Equal(0, Migrator.Run(connectionString));

        await using var conn = await PostgresFixture.OpenAsync(settings);
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> 'schemaversions'")).ToHashSet();
        Assert.Equal(new HashSet<string> { "devices", "tips", "admin_sessions" }, tables);

        await conn.ExecuteAsync("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x01')");
        await conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES ('keep me', 'published')");
        var journalBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions");

        Assert.Equal(0, Migrator.Run(connectionString));

        Assert.Equal(journalBefore, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions"));
        Assert.Equal(3, journalBefore);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM devices"));
        Assert.Equal("keep me", await conn.ExecuteScalarAsync<string>("SELECT text FROM tips"));
    }

    [Fact]
    public void Migrate_exits_with_an_error_code_when_it_fails()
    {
        var settings = new Dictionary<string, string?>
        {
            ["PGHOST"] = "127.0.0.1",
            ["PGPORT"] = PostgresFixture.UnusedPort().ToString(),
            ["PGUSER"] = "postgres",
            ["PGPASSWORD"] = "postgres",
            ["PGDATABASE"] = "none",
        };

        Assert.NotEqual(0, Migrator.Run(Database.ConnectionString(PostgresFixture.Config(settings))));
    }

    [Fact]
    public async Task The_database_rejects_text_over_2000_characters_and_unknown_status()
    {
        var settings = await _db.NewDatabaseAsync();
        await using var conn = await PostgresFixture.OpenAsync(settings);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES (repeat('x', 2001), 'draft')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES ('ok', 'archived')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES ('', 'draft')"));
    }
}
