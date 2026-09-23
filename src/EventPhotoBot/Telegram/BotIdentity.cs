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
public sealed class BotIdentity
{
    public string? Username { get; private set; }

    /// <summary>The deep link for one event's code; null while the username is unknown.</summary>
    public string? JoinUrlFor(string joinCode) =>
        Username is null ? null : $"https://t.me/{Username}?start={joinCode}";

    public async Task ResolveAsync(ITelegramClient telegram, ILogger<BotIdentity> logger)
    {
        try
        {
            Username = (await telegram.GetMeAsync())?.Username;
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
