using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class EntryTests
{
    private readonly PostgresFixture _db;

    public EntryTests(PostgresFixture db)
    {
        _db = db;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> CountEntriesAsync(TestApp app)
    {
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM entries");
    }

    [Fact]
    public async Task A_new_entry_answers_201_with_the_trimmed_text_and_belongs_to_the_calling_device()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();

        var response = await app.ClientFor(token).PostEntryAsync(EntryRequests.Body(EntryRequests.GoldenId, "  Hello\nworld  ", EntryRequests.GoldenCreatedAt));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal(EntryRequests.GoldenId, json.GetProperty("id").GetString());
        Assert.Equal(EntryRequests.GoldenText, json.GetProperty("text").GetString());
        Assert.Equal(DateTime.Parse(EntryRequests.GoldenCreatedAt).ToUniversalTime(), json.GetProperty("clientCreatedAt").GetDateTime().ToUniversalTime());
        Assert.True(json.TryGetProperty("receivedAt", out _));
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        Assert.Equal(deviceId, await conn.ExecuteScalarAsync<Guid>("SELECT device_id FROM entries WHERE id = @id", new { id = Guid.Parse(EntryRequests.GoldenId) }));
    }

    [Fact]
    public async Task The_stored_hash_of_the_golden_request_equals_the_digest_computed_outside_the_code()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();

        await app.ClientFor(token).PostEntryAsync(EntryRequests.Golden());

        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var stored = await conn.ExecuteScalarAsync<byte[]>("SELECT request_hash FROM entries");
        Assert.Equal(EntryRequests.GoldenDigest, Convert.ToHexStringLower(stored!));
    }

    [Fact]
    public async Task A_retry_after_a_lost_response_answers_200_with_the_same_row_and_stores_nothing_new()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        var first = await client.PostEntryAsync(EntryRequests.Golden());
        app.Clock.Now += TimeSpan.FromMinutes(5);
        var retry = await client.PostEntryAsync(EntryRequests.Golden());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await JsonOf(first)).GetProperty("receivedAt").GetString(), (await JsonOf(retry)).GetProperty("receivedAt").GetString());
        Assert.Equal(1, await CountEntriesAsync(app));
    }

    [Theory]
    [InlineData("Hello\nworldd", EntryRequests.GoldenCreatedAt)]
    [InlineData(EntryRequests.GoldenText, "2026-10-24T12:00:01Z")]
    [InlineData("Hello\nworld ", EntryRequests.GoldenCreatedAt)]
    public async Task The_same_id_with_different_content_answers_409_id_conflict_and_keeps_the_stored_row(string text, string createdAt)
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        await client.PostEntryAsync(EntryRequests.Golden());

        var response = await client.PostEntryAsync(EntryRequests.Body(EntryRequests.GoldenId, text, createdAt));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("id_conflict", (await JsonOf(response)).GetProperty("code").GetString());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        Assert.Equal(EntryRequests.GoldenText, await conn.ExecuteScalarAsync<string>("SELECT text FROM entries"));
        Assert.Equal(EntryRequests.GoldenDigest, Convert.ToHexStringLower((await conn.ExecuteScalarAsync<byte[]>("SELECT request_hash FROM entries"))!));
    }

    [Fact]
    public async Task The_same_id_from_another_device_answers_409_id_taken_and_shows_nothing_of_the_stored_entry()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, tokenA) = await app.RegisterAsync();
        var (_, tokenB) = await app.RegisterAsync();
        var clientA = app.ClientFor(tokenA);
        var clientB = app.ClientFor(tokenB);
        await clientA.PostEntryAsync(EntryRequests.Golden());

        // Byte-identical request, so the hash matches and only the device check stands between B and A's row.
        var response = await clientB.PostEntryAsync(EntryRequests.Golden());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("id_taken", json.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("text", out _));
        Assert.DoesNotContain("Hello", text);
        Assert.Equal(HttpStatusCode.OK, (await clientA.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        var listB = await JsonOf(await clientB.GetAsync("/api/v1/entries"));
        Assert.Equal(0, listB.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_post_whose_row_is_held_by_an_open_transaction_waits_and_then_answers_200()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        await using var holder = await PostgresFixture.OpenAsync(app.Settings);
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) VALUES (@id, @deviceId, @text, @createdAt, @hash)",
            new
            {
                id = Guid.Parse(EntryRequests.GoldenId),
                deviceId,
                text = EntryRequests.GoldenText,
                createdAt = DateTime.Parse(EntryRequests.GoldenCreatedAt).ToUniversalTime(),
                hash = Convert.FromHexString(EntryRequests.GoldenDigest),
            },
            tx);

        var post = app.ClientFor(token).PostEntryAsync(EntryRequests.Golden());
        await Task.Delay(500);
        Assert.False(post.IsCompleted);
        await tx.CommitAsync();

        var response = await post;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task Eight_identical_posts_at_once_give_one_201_and_seven_200_without_a_server_error()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        for (var round = 0; round < 3; round++)
        {
            var body = EntryRequests.Body(Guid.NewGuid().ToString(), "burst " + round, EntryRequests.GoldenCreatedAt);
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostEntryAsync(body)));
            var codes = responses.Select(r => (int)r.StatusCode).OrderBy(c => c).ToArray();

            Assert.Equal(new[] { 200, 200, 200, 200, 200, 200, 200, 201 }, codes);
        }
        Assert.Equal(3, await CountEntriesAsync(app));
    }

    public static TheoryData<string, int, string> JunkBodies()
    {
        const string id = EntryRequests.GoldenId;
        const string date = EntryRequests.GoldenCreatedAt;
        return new TheoryData<string, int, string>
        {
            { "", 400, "invalid_json" },
            { "{{{", 400, "invalid_json" },
            { "[]", 400, "invalid_json" },
            { "\"x\"", 400, "invalid_json" },
            { "null", 400, "invalid_json" },
            { "{}", 422, "invalid_id" },
            { $"{{\"id\":\"{id}\",\"text\":5,\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_text" },
            { $"{{\"id\":\"{id}\",\"text\":null,\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_text" },
            { $"{{\"text\":\"hi\",\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_id" },
            { $"{{\"id\":\"not-a-uuid\",\"text\":\"hi\",\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_id" },
            { $"{{\"id\":\"{id}\",\"text\":\"hi\",\"clientCreatedAt\":\"yesterday\"}}", 422, "invalid_created_at" },
            { $"{{\"id\":\"{id}\",\"text\":\"hi\"}}", 422, "invalid_created_at" },
            { $"{{\"id\":\"{id}\",\"text\":\"{new string('x', 2001)}\",\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_text" },
            { $"{{\"id\":\"{id}\",\"text\":\"      \",\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_text" },
            { $"{{\"id\":\"{id}\",\"text\":\"a\\u0000b\",\"clientCreatedAt\":\"{date}\"}}", 422, "invalid_text" },
            { $"{{\"id\":\"{id}\",\"text\":\"a\\ud800b\",\"clientCreatedAt\":\"{date}\"}}", 400, "invalid_json" },
            { $"{{\"id\":\"{id}\",\"text\":\"hi\",\"clientCreatedAt\":\"{date}\\n\"}}", 422, "invalid_created_at" },
        };
    }

    [Theory]
    [MemberData(nameof(JunkBodies))]
    public async Task A_bad_body_is_refused_with_its_own_code_and_stores_nothing(string body, int status, string code)
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();

        var response = await app.ClientFor(token).PostEntryAsync(body);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await JsonOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task A_body_with_invalid_utf8_inside_the_text_answers_400_and_stores_nothing()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var prefix = Encoding.UTF8.GetBytes($"{{\"id\":\"{EntryRequests.GoldenId}\",\"clientCreatedAt\":\"{EntryRequests.GoldenCreatedAt}\",\"text\":\"a");
        var suffix = Encoding.UTF8.GetBytes("b\"}");
        var content = new ByteArrayContent(prefix.Concat(new byte[] { 0xff }).Concat(suffix).ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await app.ClientFor(token).PostAsync("/api/v1/entries", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_json", (await JsonOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task A_body_that_is_not_json_by_content_type_answers_415()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        var plain = new StringContent(EntryRequests.Golden(), Encoding.UTF8, "text/plain");
        var none = new StringContent(EntryRequests.Golden(), Encoding.UTF8);
        none.Headers.ContentType = null;
        var form = new MultipartFormDataContent { { new StringContent(EntryRequests.Golden()), "entry" } };

        foreach (var content in new HttpContent[] { plain, none, form })
        {
            var response = await client.PostAsync("/api/v1/entries", content);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal("unsupported_media_type", (await JsonOf(response)).GetProperty("code").GetString());
        }
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task A_body_over_16_KiB_answers_413_whether_it_declares_its_length_or_arrives_chunked()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        var big = Encoding.UTF8.GetBytes(EntryRequests.Body(EntryRequests.GoldenId, "x", EntryRequests.GoldenCreatedAt) + new string(' ', Entries.BodyLimit));

        // The declared length is over the limit but the bytes sent are a small valid entry: only the check before reading refuses it.
        var declared = new DeclaredLengthContent(EntryRequests.Golden(), Entries.BodyLimit + 1);
        var honest = new StringContent(Encoding.UTF8.GetString(big), Encoding.UTF8, "application/json");
        var chunked = new ByteArrayContent(big);
        chunked.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        chunked.Headers.Add("X-Test-Chunked", "1");

        // The chunked arm only tests the counted read if the server really sees no Content-Length.
        var probe = new ByteArrayContent(big);
        probe.Headers.Add("X-Test-Chunked", "1");
        Assert.Equal("none", await (await client.PostAsync("/test/body-length", probe)).Content.ReadAsStringAsync());

        foreach (var content in new HttpContent[] { declared, honest, chunked })
        {
            var response = await client.PostAsync("/api/v1/entries", content);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("payload_too_large", (await JsonOf(response)).GetProperty("code").GetString());
        }
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task Sql_looking_text_is_stored_and_read_back_unchanged()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        const string text = "'); DROP TABLE entries;--";

        var response = await client.PostEntryAsync(EntryRequests.Body(EntryRequests.GoldenId, text, EntryRequests.GoldenCreatedAt));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = await JsonOf(await client.GetAsync("/api/v1/entries"));
        Assert.Equal(text, list.GetProperty("entries")[0].GetProperty("text").GetString());
        Assert.Equal(1, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task Entry_text_appears_in_no_log_line_and_in_no_refusal()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        var refused = await client.PostEntryAsync(EntryRequests.Body(EntryRequests.GoldenId, "LEAKMARKER" + new string('x', 2001), EntryRequests.GoldenCreatedAt));
        var accepted = await client.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "LEAKMARKER accepted", EntryRequests.GoldenCreatedAt));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.DoesNotContain("LEAKMARKER", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.DoesNotContain("LEAKMARKER", app.Logs.All);
    }

    [Fact]
    public async Task The_61st_post_in_an_hour_answers_429_entry_limit_and_the_window_reopens()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var (_, otherToken) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        for (var i = 0; i < 59; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        }

        var refused = await client.PostEntryAsync(EntryRequests.Golden());
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("entry_limit", (await JsonOf(refused)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await app.ClientFor(otherToken).PostEntryAsync(EntryRequests.Golden().Replace(EntryRequests.GoldenId, Guid.NewGuid().ToString()))).StatusCode);

        app.Clock.Now += TimeSpan.FromMinutes(61);
        Assert.Equal(HttpStatusCode.OK, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
    }

    [Fact]
    public async Task When_the_insert_fails_inside_the_handler_the_answer_is_500_and_never_2xx()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        await using var admin = await PostgresFixture.OpenAsync(app.Settings);
        await admin.ExecuteAsync("ALTER TABLE entries RENAME TO entries_gone");

        var response = await app.ClientFor(token).PostEntryAsync(EntryRequests.Body(EntryRequests.GoldenId, "PERSISTMARKER", EntryRequests.GoldenCreatedAt));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("PERSISTMARKER", body);
        Assert.DoesNotContain("PERSISTMARKER", app.Logs.All);
        await admin.ExecuteAsync("ALTER TABLE entries_gone RENAME TO entries");
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task The_entry_routes_answer_401_without_a_token_and_with_a_wrong_token_and_store_nothing()
    {
        await using var app = await TestApp.StartAsync(_db);
        var anonymous = app.CreateClient();
        var wrong = app.ClientFor(new string('a', Tokens.Length));

        foreach (var client in new[] { anonymous, wrong })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/entries")).StatusCode);
        }
        Assert.Equal(0, await CountEntriesAsync(app));
    }

    [Fact]
    public async Task The_list_holds_only_this_devices_entries_newest_first_inside_an_entries_object()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        var (otherId, _) = await app.RegisterAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, received_at, request_hash) VALUES " +
            "(gen_random_uuid(), @deviceId, 'oldest', now(), now() - interval '3 hours', '\\x01'), " +
            "(gen_random_uuid(), @deviceId, 'newest', now(), now() - interval '1 hour', '\\x01'), " +
            "(gen_random_uuid(), @deviceId, 'middle', now(), now() - interval '2 hours', '\\x01'), " +
            "(gen_random_uuid(), @otherId, 'not mine', now(), now(), '\\x01')",
            new { deviceId, otherId });

        var response = await app.ClientFor(token).GetAsync("/api/v1/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await JsonOf(response)).GetProperty("entries");
        Assert.Equal(new[] { "newest", "middle", "oldest" }, entries.EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToArray());
        Assert.False(entries[0].TryGetProperty("deviceId", out _));
        Assert.False(entries[0].TryGetProperty("requestHash", out _));
    }

    [Theory]
    [InlineData("", 50)]
    [InlineData("?limit=2", 2)]
    [InlineData("?limit=abc", 50)]
    [InlineData("?limit=0", 50)]
    [InlineData("?limit=-3", 50)]
    [InlineData("?limit=99999999999", 50)]
    [InlineData("?limit=1000", 100)]
    public async Task The_list_limit_defaults_to_50_and_is_capped_at_100(string query, int expected)
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) " +
            "SELECT gen_random_uuid(), @deviceId, 'row ' || n, now(), '\\x01' FROM generate_series(1, 101) AS n",
            new { deviceId });

        var response = await app.ClientFor(token).GetAsync("/api/v1/entries" + query);

        Assert.Equal(expected, (await JsonOf(response)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task The_first_shipped_request_shape_keeps_working_and_unknown_fields_change_nothing_stored()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        var secondId = Guid.NewGuid();
        var withExtra = $"{{\"id\":\"{secondId}\",\"text\":\"{EntryRequests.GoldenText.Replace("\n", "\\n")}\",\"clientCreatedAt\":\"{EntryRequests.GoldenCreatedAt}\",\"somethingNew\":true}}";

        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(withExtra)).StatusCode);

        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var hashes = (await conn.QueryAsync<byte[]>("SELECT request_hash FROM entries")).Select(Convert.ToHexStringLower).Distinct().ToArray();
        Assert.Equal(new[] { EntryRequests.GoldenDigest }, hashes);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    public async Task An_extreme_client_date_is_stored_and_listed_never_refused(string createdAt)
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        var id = Guid.NewGuid();

        var response = await client.PostEntryAsync(EntryRequests.Body(id.ToString(), "wrong clock", createdAt));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = await JsonOf(await client.GetAsync("/api/v1/entries"));
        Assert.Equal(id.ToString(), list.GetProperty("entries")[0].GetProperty("id").GetString());
    }
}
