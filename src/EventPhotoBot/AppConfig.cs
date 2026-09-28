using System.Text.RegularExpressions;

namespace EventPhotoBot;

/// <summary>
/// Every value arrives as an environment variable; the two secrets among them, the
/// bot token and the admin password, are projected from Secret Manager by Cloud Run.
/// The webhook secret and path, the cookie signing key and the retention secret are
/// derived from the bot token (see DerivedSecrets) rather than stored. Missing
/// anything is fatal at startup rather than at the first request that needs it.
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

    /// <summary>The header Cloud Scheduler sends to /internal/retention.</summary>
    public required string RetentionSecret { get; init; }

    /// <summary>
    /// Optional, and no longer set by the deployment: read once, by StateMigration, to
    /// give the default event the code already printed on the QR of a deployment from
    /// before events. Every event's code now lives in state and is rotated from admin;
    /// this is only for a local run or a hand-migrated old state file.
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

    private static readonly string[] SecretKeys = ["TELEGRAM_BOT_TOKEN", "ADMIN_PASSWORD"];

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
                "(Telegram's deep-link payload charset).");
        }

        // Trimmed: `gcloud secrets versions add --data-file=-` run interactively (as the
        // runbook and deploy script tell the operator to do) stores whatever the terminal
        // sends on Enter, trailing newline included. An untrimmed bot token would derive
        // a different webhook secret, path, cookie key and retention secret from the ones
        // the deploy scripts register (they trim too) — the worst case being a webhook
        // that never again matches what Telegram sends, silently 401-ing every update
        // with nothing in the app's own logs to explain why. Trimming a shared event
        // password costs nothing.
        var botToken = config["TELEGRAM_BOT_TOKEN"]!.Trim();
        return new AppConfig
        {
            BucketName = config["BUCKET_NAME"]?.Trim() ?? "",
            BotToken = botToken,
            WebhookSecret = DerivedSecrets.Derive(botToken, DerivedSecrets.WebhookSecretLabel),
            WebhookPath = DerivedSecrets.Derive(botToken, DerivedSecrets.WebhookPathLabel),
            AdminPassword = config["ADMIN_PASSWORD"]!.Trim(),
            CookieSigningKey = DerivedSecrets.Derive(botToken, DerivedSecrets.CookieSigningKeyLabel),
            RetentionSecret = DerivedSecrets.Derive(botToken, DerivedSecrets.RetentionSecretLabel),
            JoinCode = joinCode,
            LocalDev = localDev,
            StorageDir = config["STORAGE_DIR"]?.Trim(),
        };
    }

    /// <summary>Logs that each secret loaded. Never logs a value.</summary>
    public void LogLoaded(ILogger logger)
    {
        foreach (var key in SecretKeys) logger.LogInformation("Secret {Key} loaded.", key);
        if (JoinCode is not null) logger.LogInformation("{Key} loaded.", "JOIN_CODE");
        if (LocalDev)
            logger.LogWarning("LOCAL_DEV: storing files in {Dir}; Telegram is offline.", StorageDir);
        else
            logger.LogInformation("Bucket {Bucket}.", BucketName);
    }
}
