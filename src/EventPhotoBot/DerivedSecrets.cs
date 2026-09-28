using System.Security.Cryptography;
using System.Text;

namespace EventPhotoBot;

/// <summary>
/// The values that only have to be random and shared between the app and the deploy
/// scripts, computed from the bot token instead of being stored. Secret Manager bills
/// per active secret version, and six are free per billing account: deriving these
/// four leaves two real secrets, the bot token and the admin password.
///
/// HMAC-SHA256 with the bot token as the key and a fixed label as the message,
/// lowercase hex: 64 characters, inside Telegram's secret_token charset
/// [A-Za-z0-9_-] and safe in a URL path. infra/deploy.ps1 and deploy.yml compute the
/// same thing (openssl dgst -sha256 -hmac TOKEN) to register the webhook and the
/// retention job, so a label here must never change without changing it there too.
///
/// Knowing the bot token gives all four, which costs nothing: the token alone
/// already lets its holder take over the bot and point its webhook anywhere.
/// Replacing the token in BotFather replaces all four; the next deploy re-registers
/// the webhook and the retention job, and signs everyone out of admin.
/// </summary>
public static class DerivedSecrets
{
    public const string WebhookSecretLabel = "webhook-secret";
    public const string WebhookPathLabel = "webhook-path";
    public const string CookieSigningKeyLabel = "cookie-key";
    public const string RetentionSecretLabel = "retention-secret";

    public static string Derive(string botToken, string label) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes(label)));
}
