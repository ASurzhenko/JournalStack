using System.Security.Cryptography;
using System.Text;

namespace JournalStack.Server.Tests;

public class AdminRulesTests
{
    // Made with the framework's PasswordHasher on .NET 9.0.16, outside Admin.HashPassword. The phrase is a throwaway, not a secret.
    private const string GoldenPassword = "golden-test-phrase-only";
    private const string GoldenHash = "AQAAAAIAAYagAAAAEFuYqUAO3R4ZtkVTz8X86Kr71dAd8aQ7vEOXfLV/NsW6FnjJaB/o1rywokanWch5GA==";

    [Fact]
    public void A_hash_made_outside_our_wrapper_verifies_and_a_wrong_password_does_not()
    {
        Assert.True(Admin.PasswordMatches(GoldenHash, GoldenPassword));
        Assert.False(Admin.PasswordMatches(GoldenHash, GoldenPassword + "x"));
    }

    [Fact]
    public void A_hash_made_by_our_wrapper_verifies_and_two_hashes_of_one_password_differ()
    {
        var first = Admin.HashPassword(GoldenPassword);
        var second = Admin.HashPassword(GoldenPassword);

        Assert.True(Admin.PasswordMatches(first, GoldenPassword));
        Assert.False(Admin.PasswordMatches(first, "another-password-here"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void An_empty_stored_hash_never_matches_and_a_garbage_one_throws()
    {
        Assert.False(Admin.PasswordMatches("", GoldenPassword));
        Assert.Throws<FormatException>(() => Admin.PasswordMatches("garbage", GoldenPassword));
    }

    [Fact]
    public void The_session_hash_is_a_keyed_hash_of_the_token()
    {
        var hash = Admin.SessionHash("token-one", "key-one");

        Assert.Equal(32, hash.Length);
        Assert.NotEqual(hash, Admin.SessionHash("token-two", "key-one"));
        Assert.NotEqual(hash, Admin.SessionHash("token-one", "key-two"));
        Assert.Equal(HMACSHA256.HashData(Encoding.UTF8.GetBytes("key-one"), Encoding.UTF8.GetBytes("token-one")), hash);
    }
}
