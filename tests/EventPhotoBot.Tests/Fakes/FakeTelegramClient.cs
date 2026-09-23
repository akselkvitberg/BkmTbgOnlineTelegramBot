using EventPhotoBot.Telegram;

namespace EventPhotoBot.Tests.Fakes;

public sealed class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, string Text)> Sent { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default) =>
        Task.FromResult($"path/{fileId}");

    /// <summary>
    /// Runs synchronously at the start of every download, so a test can mutate state
    /// (through the store's lock) to simulate something happening while a real
    /// download would be in flight — an event closing, say — and see whether the
    /// code re-checks under the lock afterwards rather than trusting a stale read.
    /// </summary>
    public Action? OnDownload { get; set; }

    public Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default)
    {
        OnDownload?.Invoke();
        return Files.TryGetValue(filePath, out var bytes)
            ? Task.FromResult(bytes)
            : throw new InvalidOperationException($"No fake file at '{filePath}'.");
    }

    public Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        Sent.Add((chatId, text));
        return Task.CompletedTask;
    }

    public List<(long ChatId, long MessageId, string Emoji)> Reactions { get; } = [];
    public List<long> Left { get; } = [];
    public bool LeaveFails { get; set; }

    public Task SetMessageReactionAsync(
        long chatId, long messageId, string emoji, CancellationToken ct = default)
    {
        Reactions.Add((chatId, messageId, emoji));
        return Task.CompletedTask;
    }

    public Task<bool> LeaveChatAsync(long chatId, CancellationToken ct = default)
    {
        if (LeaveFails) return Task.FromResult(false);
        Left.Add(chatId);
        return Task.FromResult(true);
    }

    public string? Username { get; set; } = "eventphotobot";
    public bool CanReadAllGroupMessages { get; set; } = true;
    public bool GetMeThrows { get; set; }

    public Task<BotProfile?> GetMeAsync(CancellationToken ct = default) =>
        GetMeThrows
            ? throw new HttpRequestException("getMe unavailable")
            : Task.FromResult<BotProfile?>(new BotProfile(Username, CanReadAllGroupMessages));
}
