using System.Text.Json;

namespace EventPhotoBot.Telegram;

public sealed class TelegramClient(HttpClient http, AppConfig config, ILogger<TelegramClient> logger)
    : ITelegramClient
{
    private string Api => $"https://api.telegram.org/bot{config.BotToken}";

    public async Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/getFile?file_id={Uri.EscapeDataString(fileId)}", ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.GetProperty("result").GetProperty("file_path").GetString()
               ?? throw new InvalidOperationException("getFile returned no file_path.");
    }

    public async Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default) =>
        await http.GetByteArrayAsync(
            $"https://api.telegram.org/file/bot{config.BotToken}/{filePath}", ct);

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Api}/sendMessage", new { chat_id = chatId, text }, ct);
        // A failed reply must not fail the ingest that already succeeded. Logged as a
        // warning, not written to Console.Error: Cloud Run surfaces stderr at ERROR
        // severity, and a benign failed reply is not a fault worth paging on.
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("sendMessage failed: {StatusCode}", (int)response.StatusCode);
    }
}
