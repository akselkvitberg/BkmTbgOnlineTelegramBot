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

    public async Task SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/sendMessage",
            new { chat_id = chatId, text, reply_markup = Keyboard(buttons) }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("sendMessage failed: {StatusCode}", (int)response.StatusCode);
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/answerCallbackQuery",
            new { callback_query_id = callbackQueryId, text }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("answerCallbackQuery failed: {StatusCode}", (int)response.StatusCode);
    }

    public async Task EditMessageTextAsync(long chatId, long messageId, string text,
        IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/editMessageText",
            new { chat_id = chatId, message_id = messageId, text, reply_markup = Keyboard(buttons) }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("editMessageText failed: {StatusCode}", (int)response.StatusCode);
    }

    private static object Keyboard(IReadOnlyList<InlineButton> buttons) => new
    {
        inline_keyboard = buttons.Select(b => new[] { new { text = b.Text, callback_data = b.CallbackData } }),
    };

    public async Task SetMessageReactionAsync(
        long chatId, long messageId, string? emoji, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/setMessageReaction", new
        {
            chat_id = chatId,
            message_id = messageId,
            reaction = emoji is null ? Array.Empty<object>() : new object[] { new { type = "emoji", emoji } },
        }, ct);
        // Same as sendMessage: the photo is already stored. A group can also restrict
        // which reactions are allowed, which makes this fail with a 400 for reasons
        // that are nobody's fault.
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("setMessageReaction failed: {StatusCode}", (int)response.StatusCode);
    }

    public async Task<bool> LeaveChatAsync(long chatId, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/leaveChat", new { chat_id = chatId }, ct);
        if (response.IsSuccessStatusCode) return true;

        logger.LogWarning("leaveChat failed: {StatusCode}", (int)response.StatusCode);
        return false;
    }

    public async Task<BotProfile?> GetMeAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/getMe", ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("getMe failed: {StatusCode}", (int)response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var result = document.RootElement.GetProperty("result");
        return new BotProfile(
            result.TryGetProperty("username", out var username) ? username.GetString() : null,
            result.TryGetProperty("can_read_all_group_messages", out var readsAll)
            && readsAll.ValueKind == JsonValueKind.True);
    }
}
