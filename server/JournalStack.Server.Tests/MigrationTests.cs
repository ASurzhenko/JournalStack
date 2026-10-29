using System.Net;
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
    public async Task Migrate_creates_the_four_tables_and_a_second_run_changes_nothing()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        var connectionString = Database.ConnectionString(PostgresFixture.Config(settings));

        Assert.Equal(0, Migrator.Run(connectionString));

        await using var conn = await PostgresFixture.OpenAsync(settings);
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> 'schemaversions'")).ToHashSet();
        Assert.Equal(new HashSet<string> { "devices", "tips", "admin_sessions", "entries" }, tables);

        var deviceId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x01') RETURNING id");
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), @deviceId, 'keep me too', now(), '\\xabcd')",
            new { deviceId });
        await conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES ('keep me', 'published')");
        var journalBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions");

        Assert.Equal(0, Migrator.Run(connectionString));

        Assert.Equal(journalBefore, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions"));
        Assert.Equal(4, journalBefore);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM devices"));
        Assert.Equal("keep me", await conn.ExecuteScalarAsync<string>("SELECT text FROM tips"));
        Assert.Equal("keep me too", await conn.ExecuteScalarAsync<string>("SELECT text FROM entries"));
        Assert.Equal(new byte[] { 0xab, 0xcd }, await conn.ExecuteScalarAsync<byte[]>("SELECT request_hash FROM entries"));
    }

    [Fact]
    public async Task The_last_script_applied_on_top_of_older_data_leaves_every_row_as_it_was()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        var connectionString = Database.ConnectionString(PostgresFixture.Config(settings));

        // Schema N is every script except the newest one.
        var scripts = typeof(Migrator).Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql")).OrderBy(name => name).ToList();
        var olderScripts = DbUp.DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.EndsWith(".sql") && name != scripts.Last())
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();
        Assert.True(olderScripts.PerformUpgrade().Successful);

        await using var conn = await PostgresFixture.OpenAsync(settings);
        var deviceId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x0102') RETURNING id");
        await conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES ('older tip', 'published')");
        const string devicesSql = "SELECT id::text || '|' || encode(token_hash, 'hex') || '|' || created_at::text FROM devices ORDER BY id";
        const string tipsSql = "SELECT text || '|' || status FROM tips ORDER BY id";
        var devicesBefore = (await conn.QueryAsync<string>(devicesSql)).ToList();
        var tipsBefore = (await conn.QueryAsync<string>(tipsSql)).ToList();
        var journalBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions");

        Assert.Equal(0, Migrator.Run(connectionString));

        Assert.Equal(journalBefore + 1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schemaversions"));
        Assert.Equal(devicesBefore, (await conn.QueryAsync<string>(devicesSql)).ToList());
        Assert.Equal(tipsBefore, (await conn.QueryAsync<string>(tipsSql)).ToList());
        Assert.Equal(deviceId, await conn.ExecuteScalarAsync<Guid>("SELECT id FROM devices"));
    }

    [Fact]
    public async Task A_migration_that_only_adds_a_column_keeps_old_entries_and_the_old_request_shape_still_gets_201()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);

        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        const string entriesSql = "SELECT id::text || '|' || device_id::text || '|' || text || '|' || client_created_at::text || '|' || received_at::text || '|' || encode(request_hash, 'hex') FROM entries ORDER BY id";
        var entriesBefore = (await conn.QueryAsync<string>(entriesSql)).ToList();
        Assert.Single(entriesBefore);

        // The next release adds a nullable column. The script is the test's own, so the test does not depend on what the next real script is.
        var additive = DbUp.DeployChanges.To
            .PostgresqlDatabase(Database.ConnectionString(PostgresFixture.Config(app.Settings)))
            .WithScript("9999_test_add_column.sql", "ALTER TABLE entries ADD COLUMN mood integer;")
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();
        Assert.True(additive.PerformUpgrade().Successful);

        Assert.Equal(entriesBefore, (await conn.QueryAsync<string>(entriesSql)).ToList());
        Assert.Null(await conn.ExecuteScalarAsync<int?>("SELECT mood FROM entries"));

        // A client built before the migration sends exactly what it sent before.
        Assert.Equal(HttpStatusCode.OK, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        var newEntry = EntryRequests.Body("5c1f3b0e-3b7a-4a54-8f0e-2d6b8a4f9c01", "written after the migration", EntryRequests.GoldenCreatedAt);
        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(newEntry)).StatusCode);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM entries"));
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

    [Fact]
    public async Task The_database_rejects_an_entry_for_an_unknown_device_empty_or_long_text_and_a_null_hash()
    {
        var settings = await _db.NewDatabaseAsync();
        await using var conn = await PostgresFixture.OpenAsync(settings);
        var deviceId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x01') RETURNING id");

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync(
                "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), gen_random_uuid(), 'ok', now(), '\\x01')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync(
                "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), @deviceId, '', now(), '\\x01')",
                new { deviceId }));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync(
                "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), @deviceId, repeat('x', 2001), now(), '\\x01')",
                new { deviceId }));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync(
                "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), @deviceId, 'ok', now(), NULL)",
                new { deviceId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM entries"));
    }

    [Fact]
    public async Task Deleting_a_device_deletes_its_entries()
    {
        var settings = await _db.NewDatabaseAsync();
        await using var conn = await PostgresFixture.OpenAsync(settings);
        var deviceId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x01') RETURNING id");
        var otherId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO devices (id, token_hash) VALUES (gen_random_uuid(), '\\x02') RETURNING id");
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (gen_random_uuid(), @deviceId, 'mine', now(), '\\x01'), (gen_random_uuid(), @otherId, 'theirs', now(), '\\x01')",
            new { deviceId, otherId });

        await conn.ExecuteAsync("DELETE FROM devices WHERE id = @deviceId", new { deviceId });

        Assert.Equal("theirs", await conn.ExecuteScalarAsync<string>("SELECT text FROM entries"));
    }
}
