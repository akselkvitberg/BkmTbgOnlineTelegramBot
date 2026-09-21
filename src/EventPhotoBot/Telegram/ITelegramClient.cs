namespace EventPhotoBot.Telegram;

public interface ITelegramClient
{
    Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default);
    Task SendMessageAsync(long chatId, string text, CancellationToken ct = default);

    /// <summary>The bot's own @username, or null if Telegram would not say.</summary>
    Task<string?> GetMeAsync(CancellationToken ct = default);
}
