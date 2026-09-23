using System.Text.RegularExpressions;

namespace EventPhotoBot;

/// <summary>
/// Every value arrives as an environment variable; the secrets among them are
/// projected from Secret Manager by Cloud Run. Missing anything is fatal at
/// startup rather than at the first request that needs it.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Empty when running locally; see LocalDev.</summary>
    public required string BucketName { get; init; }
    public required string BotToken { get; init; }
    public required string WebhookSecret { get; init; }
    public required string WebhookPath { get; init; }
    public required string AdminPassword { get; init; }
    public required string CookieSigningKey { get; init; }

    /// <summary>
    /// Optional. Read once, by StateMigration, to give the default event the code
    /// already printed on the QR of a deployment from before events. After that every
    /// event's code lives in state and is rotated from admin.
    /// </summary>
    public string? JoinCode { get; init; }

    /// <summary>
    /// LOCAL_DEV=true runs the app on a workstation: photos and state go to
    /// StorageDir on disk instead of the bucket, and Telegram is replaced by an
    /// offline stand-in driven from the /dev page. Program refuses it outside the
    /// Development environment.
    /// </summary>
    public bool LocalDev { get; init; }
    public string? StorageDir { get; init; }

    /// <summary>
    /// Optional. The header Cloud Scheduler sends to /internal/retention. Unset, the
    /// route answers 404 and photos are only removed by hand.
    /// </summary>
    public string? RetentionSecret { get; init; }

    private static readonly string[] SecretKeys =
    [
        "TELEGRAM_BOT_TOKEN", "TELEGRAM_WEBHOOK_SECRET", "TELEGRAM_WEBHOOK_PATH",
        "ADMIN_PASSWORD", "COOKIE_SIGNING_KEY",
    ];

    // Telegram's deep-link payload charset. A code outside it produces a
    // https://t.me/<bot>?start=<code> link whose payload Telegram silently drops,
    // so every scan lands in the chat with no code attached and the person is
    // declined with no clue why. Failing the deploy is the cheap end of that.
    private static readonly Regex JoinCodePattern =
        new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static AppConfig Load(IConfiguration config)
    {
        var localDev = bool.TryParse(config["LOCAL_DEV"], out var flag) && flag;
        string[] required = localDev
            ? ["STORAGE_DIR", .. SecretKeys]
            : ["BUCKET_NAME", .. SecretKeys];

        var missing = required.Where(k => string.IsNullOrWhiteSpace(config[k])).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required configuration: {string.Join(", ", missing)}. " +
                "Secrets come from Secret Manager via Cloud Run; check the service's env vars.");
        }

        var joinCode = config["JOIN_CODE"]?.Trim();
        if (string.IsNullOrEmpty(joinCode))
        {
            joinCode = null;
        }
        else if (!JoinCodePattern.IsMatch(joinCode))
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
            BucketName = config["BUCKET_NAME"]?.Trim() ?? "",
            BotToken = config["TELEGRAM_BOT_TOKEN"]!.Trim(),
            WebhookSecret = config["TELEGRAM_WEBHOOK_SECRET"]!.Trim(),
            WebhookPath = config["TELEGRAM_WEBHOOK_PATH"]!.Trim(),
            AdminPassword = config["ADMIN_PASSWORD"]!.Trim(),
            CookieSigningKey = config["COOKIE_SIGNING_KEY"]!.Trim(),
            JoinCode = joinCode,
            LocalDev = localDev,
            StorageDir = config["STORAGE_DIR"]?.Trim(),
            RetentionSecret = string.IsNullOrWhiteSpace(config["RETENTION_SECRET"]) ? null : config["RETENTION_SECRET"]!.Trim(),
        };
    }

    /// <summary>Logs that each secret loaded. Never logs a value.</summary>
    public void LogLoaded(ILogger logger)
    {
        foreach (var key in SecretKeys) logger.LogInformation("Secret {Key} loaded.", key);
        if (JoinCode is not null) logger.LogInformation("Secret {Key} loaded.", "JOIN_CODE");
        if (RetentionSecret is not null) logger.LogInformation("Secret {Key} loaded.", "RETENTION_SECRET");
        if (LocalDev)
            logger.LogWarning("LOCAL_DEV: storing files in {Dir}; Telegram is offline.", StorageDir);
        else
            logger.LogInformation("Bucket {Bucket}.", BucketName);
    }
}
