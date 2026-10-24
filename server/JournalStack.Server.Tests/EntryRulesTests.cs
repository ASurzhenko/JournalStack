namespace JournalStack.Server.Tests;

public class EntryRulesTests
{
    // Digests computed outside the code with: printf '<text>\n<date>\n' | sha256sum
    private const string HelloDigest = "b72d21ada45386dcd5494bc0bf731d0ee32750ed23a321b995b67670cad060ad";
    private const string UnicodeDigest = "bb8ecef12df8f2c47ee030608d604dfdd9096f381730931448f0486533ba2397";
    private const string OuterSpacesDigest = "50e8d68f7a03d51e104c9b47b627efd15ddd88a92e47902a4c15b5e2a12dbe46";

    [Fact]
    public void Hash_matches_digests_computed_outside_the_code()
    {
        Assert.Equal(HelloDigest, Convert.ToHexStringLower(Entries.Hash("Hello\nworld", "2026-10-24T12:00:00Z")));
        Assert.Equal(UnicodeDigest, Convert.ToHexStringLower(Entries.Hash("Привіт, світе \U0001F600", "2026-10-24T12:00:00.1234567+03:00")));
        Assert.Equal(OuterSpacesDigest, Convert.ToHexStringLower(Entries.Hash(" Hello\nworld ", "2026-10-24T12:00:00Z")));
    }

    [Fact]
    public void Hash_changes_with_any_character_and_covers_outer_whitespace()
    {
        var baseline = Entries.Hash("Hello", "2026-10-24T12:00:00Z");

        Assert.NotEqual(baseline, Entries.Hash("Hellp", "2026-10-24T12:00:00Z"));
        Assert.NotEqual(baseline, Entries.Hash("Hello", "2026-10-24T12:00:01Z"));
        Assert.NotEqual(baseline, Entries.Hash("Hello ", "2026-10-24T12:00:00Z"));
    }

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("  hi  ", "hi")]
    [InlineData("a\tb\nc\r\nd", "a\tb\nc\r\nd")]
    public void Text_is_accepted_and_trimmed(string raw, string expected)
    {
        Assert.True(Entries.TryCleanText(raw, out var text));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Text_length_is_counted_in_utf16_units_after_trimming()
    {
        Assert.True(Entries.TryCleanText(new string('x', 2000), out _));
        Assert.False(Entries.TryCleanText(new string('x', 2001), out _));
        Assert.True(Entries.TryCleanText("  " + new string('x', 2000) + "  ", out _));
        Assert.True(Entries.TryCleanText(string.Concat(Enumerable.Repeat("\U0001F600", 1000)), out _));
        Assert.False(Entries.TryCleanText(string.Concat(Enumerable.Repeat("\U0001F600", 1001)), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ")]
    [InlineData("a\u0000b")]
    [InlineData("a\u0007b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    public void Text_is_refused(string? raw)
    {
        Assert.False(Entries.TryCleanText(raw, out _));
    }

    [Theory]
    [InlineData("0b8e0d4e-6d0f-4f3b-9a53-0f2b7b6f3a11")]
    [InlineData("0B8E0D4E-6D0F-4F3B-9A53-0F2B7B6F3A11")]
    public void Id_is_accepted(string raw)
    {
        Assert.True(Entries.TryParseId(raw, out var id));
        Assert.NotEqual(Guid.Empty, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("junk")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{0b8e0d4e-6d0f-4f3b-9a53-0f2b7b6f3a11}")]
    [InlineData("0b8e0d4e6d0f4f3b9a530f2b7b6f3a11")]
    public void Id_is_refused(string? raw)
    {
        Assert.False(Entries.TryParseId(raw, out _));
    }

    [Theory]
    [InlineData("2026-10-24T12:00:00Z")]
    [InlineData("2026-10-24T12:00:00.1234567Z")]
    [InlineData("2026-10-24T12:00:00.123Z")]
    [InlineData("2026-10-24T12:00:00+03:00")]
    [InlineData("2026-10-24T12:00:00-05:30")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    public void Created_at_is_accepted(string raw)
    {
        Assert.True(Entries.TryParseCreatedAt(raw, out var utc));
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-24T12:00:00")]
    [InlineData("2026-10-24")]
    [InlineData("2026-10-24T12:00:00Z\n")]
    [InlineData(" 2026-10-24T12:00:00Z")]
    [InlineData("2026-10-24T12:00:00.12345678Z")]
    [InlineData("2026-10-24t12:00:00Z")]
    [InlineData("2026-10-24T12:00:00z")]
    [InlineData("0001-01-01T00:00:00+03:00")]
    public void Created_at_is_refused(string? raw)
    {
        Assert.False(Entries.TryParseCreatedAt(raw, out _));
    }

    [Fact]
    public void Created_at_with_an_offset_is_converted_to_utc()
    {
        Assert.True(Entries.TryParseCreatedAt("2026-10-24T12:00:00+03:00", out var utc));
        Assert.Equal(new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), utc);
    }
}
