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

    private static readonly string[] SecretKeys =
    [
        "TELEGRAM_BOT_TOKEN", "TELEGRAM_WEBHOOK_SECRET", "TELEGRAM_WEBHOOK_PATH",
        "ADMIN_PASSWORD", "COOKIE_SIGNING_KEY",
    ];

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

        return new AppConfig
        {
            BucketName = config["BUCKET_NAME"]!,
            EventName = config["EVENT_NAME"]!,
            BotToken = config["TELEGRAM_BOT_TOKEN"]!,
            WebhookSecret = config["TELEGRAM_WEBHOOK_SECRET"]!,
            WebhookPath = config["TELEGRAM_WEBHOOK_PATH"]!,
            AdminPassword = config["ADMIN_PASSWORD"]!,
            CookieSigningKey = config["COOKIE_SIGNING_KEY"]!,
        };
    }

    /// <summary>Logs that each secret loaded. Never logs a value.</summary>
    public void LogLoaded(ILogger logger)
    {
        foreach (var key in SecretKeys) logger.LogInformation("Secret {Key} loaded.", key);
        logger.LogInformation("Bucket {Bucket}, event {Event}.", BucketName, EventName);
    }
}
