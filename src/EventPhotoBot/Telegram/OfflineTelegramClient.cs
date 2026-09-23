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
    private long _nextMessageId;

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
        Record(new BotReply(chatId, text, DateTimeOffset.UtcNow, MessageId: Interlocked.Increment(ref _nextMessageId)));
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons,
        CancellationToken ct = default)
    {
        logger.LogInformation("Bot → {ChatId}: {Text}", chatId, text);
        Record(new BotReply(chatId, text, DateTimeOffset.UtcNow,
            MessageId: Interlocked.Increment(ref _nextMessageId), Buttons: buttons));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        logger.LogInformation("Bot answers callback {CallbackQueryId}: {Text}", callbackQueryId, text);
        if (text is not null) Record(new BotReply(0, text, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task EditMessageTextAsync(long chatId, long messageId, string text,
        IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)
    {
        logger.LogInformation("Bot edits message {MessageId} in {ChatId}: {Text}", messageId, chatId, text);
        Record(new BotReply(chatId, text, DateTimeOffset.UtcNow, MessageId: messageId, Buttons: buttons, Edited: true));
        return Task.CompletedTask;
    }

    public Task SetMessageReactionAsync(
        long chatId, long messageId, string emoji, CancellationToken ct = default)
    {
        logger.LogInformation("Bot → {ChatId}: {Emoji} on message {MessageId}", chatId, emoji, messageId);
        Record(new BotReply(chatId, emoji, DateTimeOffset.UtcNow, Reaction: true));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing to leave offline. The /dev page tells the handler the bot was removed,
    /// the way Telegram would, when it simulates a group.
    /// </summary>
    public Task<bool> LeaveChatAsync(long chatId, CancellationToken ct = default)
    {
        logger.LogInformation("Bot left {ChatId}", chatId);
        return Task.FromResult(true);
    }

    private void Record(BotReply reply)
    {
        _replies.Enqueue(reply);
        while (_replies.Count > MaxReplies) _replies.TryDequeue(out _);
    }

    public Task<BotProfile?> GetMeAsync(CancellationToken ct = default) =>
        Task.FromResult<BotProfile?>(new BotProfile(Username, CanReadAllGroupMessages: true));
}

/// <summary>A message the bot sent, or, with <see cref="Reaction"/>, an emoji it reacted with.</summary>
public sealed record BotReply(long ChatId, string Text, DateTimeOffset At, bool Reaction = false,
    long MessageId = 0, IReadOnlyList<InlineButton>? Buttons = null, bool Edited = false);
