using EventPhotoBot.Telegram;

namespace EventPhotoBot.Tests.Fakes;

public sealed class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, string Text)> Sent { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default) =>
        Task.FromResult($"path/{fileId}");

    public Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default) =>
        Files.TryGetValue(filePath, out var bytes)
            ? Task.FromResult(bytes)
            : throw new InvalidOperationException($"No fake file at '{filePath}'.");

    public Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        Sent.Add((chatId, text));
        return Task.CompletedTask;
    }

    public string? Username { get; set; } = "eventphotobot";
    public bool GetMeThrows { get; set; }

    public Task<string?> GetMeAsync(CancellationToken ct = default) =>
        GetMeThrows
            ? throw new HttpRequestException("getMe unavailable")
            : Task.FromResult(Username);
}
