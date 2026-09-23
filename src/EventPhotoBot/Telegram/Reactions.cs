using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

/// <summary>
/// The bot's reaction on a photo posted in a group: how the member learns what became
/// of it without a reply in everybody's chat. Two, so a photographer can tell "on the
/// screen" from "queued"; none once it is not going to be shown.
/// </summary>
public static class Reactions
{
    public const string Queued = "👀";
    public const string Live = "🔥";

    public static string? For(ImageStatus status) => status switch
    {
        ImageStatus.Pending => Queued,
        ImageStatus.Approved => Live,
        _ => null,
    };

    /// <summary>
    /// Brings the reaction in line with the photo's status. Best effort: the decision
    /// is already written, and a group can have left, deleted the message or banned
    /// the reaction, none of which makes the decision wrong.
    /// </summary>
    public static Task SyncAsync(ITelegramClient telegram, ImageRecord image, ILogger logger, CancellationToken ct) =>
        SetAsync(telegram, image, For(image.Status), logger, ct);

    public static Task ClearAsync(ITelegramClient telegram, ImageRecord image, ILogger logger, CancellationToken ct) =>
        SetAsync(telegram, image, null, logger, ct);

    private static async Task SetAsync(ITelegramClient telegram, ImageRecord image, string? emoji,
        ILogger logger, CancellationToken ct)
    {
        if (image.TelegramChatId is not { } chat || image.TelegramMessageId is not { } message) return;
        try
        {
            await telegram.SetMessageReactionAsync(chat, message, emoji, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not update the reaction on image {ImageId}.", image.Id);
        }
    }
}
