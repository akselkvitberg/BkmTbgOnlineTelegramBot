namespace EventPhotoBot.Telegram;

public interface ITelegramClient
{
    Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default);
    Task SendMessageAsync(long chatId, string text, CancellationToken ct = default);
}
