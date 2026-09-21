using System.Security.Cryptography;
using System.Text;

namespace EventPhotoBot.Web;

/// <summary>
/// A session is "someone typed the password". There is one user, so the cookie
/// carries an expiry and an HMAC over it, and nothing else.
/// </summary>
public static class SessionCookie
{
    // Not a cosmetic name. Firebase Hosting fronts this service (see
    // infra/main.tf's hosting_site) and strips every incoming cookie except
    // the specially-named __session before the request reaches Cloud Run, so
    // any other name means the session cookie is set by the browser, sent by
    // the browser, and then silently discarded in front of the app — login
    // appears to do nothing at all on the .web.app URL while working fine on
    // the run.app one. Hosting also folds __session into its cache key, so a
    // signed-in response cannot be served to a different visitor.
    public const string Name = "__session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static string Issue(string signingKey, DateTimeOffset expiresAt)
    {
        var payload = expiresAt.ToUnixTimeSeconds().ToString();
        return $"{payload}.{Sign(signingKey, payload)}";
    }

    public static bool IsValid(string signingKey, string? cookieValue, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieValue)) return false;

        var separator = cookieValue.IndexOf('.');
        if (separator <= 0 || separator == cookieValue.Length - 1) return false;

        var payload = cookieValue[..separator];
        var signature = cookieValue[(separator + 1)..];

        if (!long.TryParse(payload, out var expiresAtUnix)) return false;

        var expected = Sign(signingKey, payload);
        if (!FixedTimeEquals(expected, signature)) return false;

        return DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) > now;
    }

    public static bool PasswordMatches(string expected, string? supplied) =>
        supplied is not null && FixedTimeEquals(expected, supplied);

    private static string Sign(string signingKey, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Hashes both sides before comparing so the comparison is constant time
    /// regardless of length — FixedTimeEquals alone leaks length via its argument check.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
