namespace EventPhotoBot.Telegram;

/// <summary>
/// The bot's own username, and the join deep link built from it. Resolved rather
/// than configured because the username is Telegram's to know, not the operator's
/// to retype correctly.
///
/// Fetched lazily, the first time something asks, and cached once known. Never at
/// startup: a slow Telegram there once held the instance off its port long enough
/// for Cloud Run to kill it, which cost a guest a 503 for the sake of a QR code.
///
/// A failure here is deliberately not fatal. A missing QR costs one affordance;
/// refusing to serve costs the event its screen. A failure is not cached forever
/// either, but it is remembered for <see cref="RetryAfter"/>, so the manifest poll
/// that asks every two seconds does not turn a Telegram outage into a stream of
/// getMe calls, each one holding up a poll.
/// </summary>
public sealed class BotIdentity(AppConfig config, ITelegramClient telegram, ILogger<BotIdentity> logger)
{
    /// <summary>How long one getMe may hold up the request that triggered it.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long after a failed lookup the next one is tried.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private Task<string?>? _lookup;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;
    private string? _username;

    /// <summary>
    /// The deep link, or null while the username is unknown. Only the first caller
    /// (or the first after a failure has aged out) talks to Telegram; everyone else
    /// shares that lookup or gets the cached answer.
    /// </summary>
    public async Task<string?> GetJoinUrlAsync(CancellationToken ct = default)
    {
        var username = await GetUsernameAsync(ct);
        return username is null ? null : $"https://t.me/{username}?start={config.JoinCode}";
    }

    public Task<string?> GetUsernameAsync(CancellationToken ct = default)
    {
        Task<string?> lookup;
        lock (_lock)
        {
            if (_username is not null) return Task.FromResult<string?>(_username);
            if (_lookup is null)
            {
                if (DateTimeOffset.UtcNow - _failedAt < RetryAfter) return Task.FromResult<string?>(null);
                _lookup = LookupAsync();
            }
            lookup = _lookup;
        }

        // The lookup itself is not tied to this caller: if this request goes away,
        // the answer still lands in the cache for the next one.
        return lookup.WaitAsync(ct);
    }

    private async Task<string?> LookupAsync()
    {
        string? username = null;
        try
        {
            using var timeout = new CancellationTokenSource(LookupTimeout);
            username = (await telegram.GetMeAsync(timeout.Token))?.Username;
            if (username is null)
                logger.LogWarning("Telegram returned no username; the join QR is omitted for now.");
            else
                logger.LogInformation("Bot username resolved.");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resolve the bot username; the join QR is omitted for now.");
        }

        lock (_lock)
        {
            _username = username;
            if (username is null) _failedAt = DateTimeOffset.UtcNow;
            _lookup = null;
        }
        return username;
    }

    /// <summary>
    /// Drops the cached username and holds off the next lookup, so a test can
    /// exercise the degraded path. Public rather than internal: the test project is
    /// a separate assembly and this codebase sets no InternalsVisibleTo.
    /// </summary>
    public void Forget()
    {
        lock (_lock)
        {
            _username = null;
            _failedAt = DateTimeOffset.UtcNow;
        }
    }
}
