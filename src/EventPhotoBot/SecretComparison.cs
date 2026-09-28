using System.Security.Cryptography;
using System.Text;

namespace EventPhotoBot;

public static class SecretComparison
{
    /// <summary>
    /// Constant-time over the UTF-8 bytes, hashing both sides before comparing:
    /// CryptographicOperations.FixedTimeEquals alone still leaks length through its
    /// own argument check unless both inputs are already the same size, and hashing
    /// first fixes that at 32 bytes regardless of what was supplied — a missing value
    /// (null) hashes and compares exactly like a present-but-wrong one. Used for every
    /// secret a stranger can reach: the webhook secret, join codes, the retention secret.
    /// </summary>
    public static bool Matches(string expected, string? supplied)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
