namespace EventPhotoBot.Telegram;

public interface ITelegramClient
{
    Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default);
    Task SendMessageAsync(long chatId, string text, CancellationToken ct = default);

    /// <summary>
    /// Puts one emoji reaction on a message. How the bot acknowledges a photo in a
    /// group, where a text reply per photo would be noise in everybody's chat.
    /// </summary>
    Task SetMessageReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct = default);

    /// <summary>Takes the bot out of a group. False if Telegram refused.</summary>
    Task<bool> LeaveChatAsync(long chatId, CancellationToken ct = default);

    /// <summary>What Telegram says about the bot itself, or null if it would not say.</summary>
    Task<BotProfile?> GetMeAsync(CancellationToken ct = default);
}

/// <param name="Username">The bot's own @username, without the @.</param>
/// <param name="CanReadAllGroupMessages">
/// False while BotFather's privacy mode is on. The bot then sees only commands and
/// replies in a group, never the photos members post there.
/// </param>
public sealed record BotProfile(string? Username, bool CanReadAllGroupMessages);
