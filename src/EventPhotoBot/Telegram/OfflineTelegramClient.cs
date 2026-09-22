using System.Collections.Concurrent;

namespace EventPhotoBot.Telegram;

/// <summary>
/// Stands in for Telegram when the app runs locally (LOCAL_DEV). Nothing leaves the
/// machine: files a simulated guest "sends" are staged here by the /dev page, and
/// the bot's replies are kept so that page can show them.
/// </summary>
public sealed class OfflineTelegramClient(ILogger<OfflineTelegramClient> logger) : ITelegramClient
{
    public const string Username = "local_dev_bot";
    private const int MaxReplies = 50;

    private readonly ConcurrentDictionary<string, byte[]> _staged = new();
    private readonly ConcurrentQueue<BotReply> _replies = new();

    /// <summary>Holds bytes under a file id, the way Telegram holds an upload.</summary>
    public string Stage(byte[] bytes)
    {
        var fileId = Guid.NewGuid().ToString("N");
        _staged[fileId] = bytes;
        return fileId;
    }

    public IReadOnlyList<BotReply> Replies => _replies.Reverse().ToList();

    public Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default) =>
        _staged.ContainsKey(fileId)
            ? Task.FromResult(fileId)
            : throw new InvalidOperationException($"No staged file '{fileId}'.");

    public Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default) =>
        _staged.TryRemove(filePath, out var bytes)
            ? Task.FromResult(bytes)
            : throw new InvalidOperationException($"No staged file '{filePath}'.");

    public Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        logger.LogInformation("Bot → {ChatId}: {Text}", chatId, text);
        _replies.Enqueue(new BotReply(chatId, text, DateTimeOffset.UtcNow));
        while (_replies.Count > MaxReplies) _replies.TryDequeue(out _);
        return Task.CompletedTask;
    }

    public Task<string?> GetMeAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(Username);
}

public sealed record BotReply(long ChatId, string Text, DateTimeOffset At);
