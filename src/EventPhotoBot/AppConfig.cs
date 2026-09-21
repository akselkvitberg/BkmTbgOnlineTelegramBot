using System.Text.RegularExpressions;

namespace EventPhotoBot;

/// <summary>
/// Every value arrives as an environment variable; the secrets among them are
/// projected from Secret Manager by Cloud Run. Missing anything is fatal at
/// startup rather than at the first request that needs it.
/// </summary>
public sealed class AppConfig
{
    public required string BucketName { get; init; }
    public required string EventName { get; init; }
    public required string BotToken { get; init; }
    public required string WebhookSecret { get; init; }
    public required string WebhookPath { get; init; }
    public required string AdminPassword { get; init; }
    public required string CookieSigningKey { get; init; }
    public required string JoinCode { get; init; }

    private static readonly string[] SecretKeys =
    [
        "TELEGRAM_BOT_TOKEN", "TELEGRAM_WEBHOOK_SECRET", "TELEGRAM_WEBHOOK_PATH",
        "ADMIN_PASSWORD", "COOKIE_SIGNING_KEY", "JOIN_CODE",
    ];

    // Telegram's deep-link payload charset. A code outside it produces a
    // https://t.me/<bot>?start=<code> link whose payload Telegram silently drops,
    // so every scan lands in the chat with no code attached and the person is
    // declined with no clue why. Failing the deploy is the cheap end of that.
    private static readonly Regex JoinCodePattern =
        new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static AppConfig Load(IConfiguration config)
    {
        string[] required =
        [
            "BUCKET_NAME", "EVENT_NAME", .. SecretKeys,
        ];

        var missing = required.Where(k => string.IsNullOrWhiteSpace(config[k])).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required configuration: {string.Join(", ", missing)}. " +
                "Secrets come from Secret Manager via Cloud Run; check the service's env vars.");
        }

        var joinCode = config["JOIN_CODE"]!.Trim();
        if (!JoinCodePattern.IsMatch(joinCode))
        {
            throw new InvalidOperationException(
                "JOIN_CODE must be 1-64 characters from A-Z, a-z, 0-9, underscore or hyphen " +
                "(Telegram's deep-link payload charset). Fix the secret version and redeploy.");
        }

        // Trimmed: `gcloud secrets versions add --data-file=-` run interactively (as the
        // runbook and deploy script tell the operator to do) stores whatever the terminal
        // sends on Enter, trailing newline included. An untrimmed value flows straight into
        // the HMAC key, the password comparison, the route template and the constant-time
        // webhook check — the worst case being a webhook secret that never again matches
        // what Telegram sends, silently 401-ing every update with nothing in the app's own
        // logs to explain why. Trimming a shared event password costs nothing.
        return new AppConfig
        {
            BucketName = config["BUCKET_NAME"]!.Trim(),
            EventName = config["EVENT_NAME"]!.Trim(),
            BotToken = config["TELEGRAM_BOT_TOKEN"]!.Trim(),
            WebhookSecret = config["TELEGRAM_WEBHOOK_SECRET"]!.Trim(),
            WebhookPath = config["TELEGRAM_WEBHOOK_PATH"]!.Trim(),
            AdminPassword = config["ADMIN_PASSWORD"]!.Trim(),
            CookieSigningKey = config["COOKIE_SIGNING_KEY"]!.Trim(),
            JoinCode = joinCode,
        };
    }

    /// <summary>Logs that each secret loaded. Never logs a value.</summary>
    public void LogLoaded(ILogger logger)
    {
        foreach (var key in SecretKeys) logger.LogInformation("Secret {Key} loaded.", key);
        logger.LogInformation("Bucket {Bucket}, event {Event}.", BucketName, EventName);
    }
}
