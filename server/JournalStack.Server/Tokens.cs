using System.Security.Cryptography;
using System.Text;

namespace JournalStack.Server;

public static class Tokens
{
    // 32 random bytes as base64url without padding.
    public const int Length = 43;

    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Only the hash is stored, so a copy of the database does not contain usable tokens.
    public static byte[] Hash(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}
