namespace EventPhotoBot.Telegram;

/// <summary>
/// The bot's own username, learned once at startup, and the join deep link built
/// from it. Resolved rather than configured because the username is Telegram's to
/// know, not the operator's to retype correctly.
///
/// A failure here is deliberately not fatal. A missing QR costs one affordance;
/// refusing to start costs the event its screen. This is the opposite of the
/// missing-secret case, where the service genuinely cannot work.
/// </summary>
public sealed class BotIdentity(AppConfig config)
{
    public string? Username { get; private set; }

    public string? JoinUrl =>
        Username is null ? null : $"https://t.me/{Username}?start={config.JoinCode}";

    /// <summary>
    /// How long startup waits for getMe. This runs before the server listens, so
    /// Cloud Run's startup probe is failing the whole time; the HttpClient's own
    /// 60s timeout outlasts the probe's ~33s window, and a stalled call would get
    /// the instance killed instead of merely losing the QR.
    /// </summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    public async Task ResolveAsync(ITelegramClient telegram, ILogger<BotIdentity> logger)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        try
        {
            Username = (await telegram.GetMeAsync(timeout.Token))?.Username;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resolve the bot username; the join QR will be omitted.");
            return;
        }

        if (Username is null)
            logger.LogWarning("Telegram returned no username; the join QR will be omitted.");
        else
            logger.LogInformation("Bot username resolved.");
    }

    /// <summary>
    /// Drops the resolved username, so a test can exercise the degraded path.
    /// Public rather than internal: the test project is a separate assembly and
    /// this codebase sets no InternalsVisibleTo.
    /// </summary>
    public void Forget() => Username = null;
}
