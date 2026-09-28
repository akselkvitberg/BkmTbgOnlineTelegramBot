using System.Text.Json.Serialization;

namespace EventPhotoBot.Telegram;

public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }

    /// <summary>The bot itself was added to, or removed from, a chat.</summary>
    [JsonPropertyName("my_chat_member")] public TgChatMemberUpdated? MyChatMember { get; set; }

    /// <summary>Someone tapped one of the bot's inline buttons.</summary>
    [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }
}

public sealed class TgCallbackQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public TgUser? From { get; set; }

    /// <summary>The bot's own message the button was on. Absent if it is too old for Telegram to say.</summary>
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }

    [JsonPropertyName("data")] public string? Data { get; set; }
}

/// <summary>One inline button under a message. Telegram caps CallbackData at 64 bytes.</summary>
public sealed record InlineButton(string Text, string CallbackData);

public sealed class TgChatMemberUpdated
{
    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("new_chat_member")] public TgChatMember? NewChatMember { get; set; }
}

public sealed class TgChatMember
{
    /// <summary>"creator", "administrator", "member", "restricted", "left" or "kicked".</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>Only meaningful for "restricted": whether they are still in the chat.</summary>
    [JsonPropertyName("is_member")] public bool? IsMember { get; set; }

    public bool IsInChat => Status switch
    {
        "creator" or "administrator" or "member" => true,
        "restricted" => IsMember ?? false,
        _ => false,
    };
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("from")] public TgUser? From { get; set; }

    /// <summary>
    /// Set when a group message was posted on behalf of a chat rather than a person —
    /// an anonymous admin, or a linked channel. <see cref="From"/> is then a
    /// placeholder account shared by everyone posting that way, not a member.
    /// </summary>
    [JsonPropertyName("sender_chat")] public TgChat? SenderChat { get; set; }

    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("media_group_id")] public string? MediaGroupId { get; set; }
    [JsonPropertyName("photo")] public List<TgPhotoSize>? Photo { get; set; }
    [JsonPropertyName("document")] public TgDocument? Document { get; set; }

    /// <summary>
    /// Service messages Telegram posts when a basic group is upgraded to a supergroup,
    /// which gives it a new chat id: "to" arrives in the old group, "from" in the new.
    /// </summary>
    [JsonPropertyName("migrate_to_chat_id")] public long? MigrateToChatId { get; set; }
    [JsonPropertyName("migrate_from_chat_id")] public long? MigrateFromChatId { get; set; }
}

public sealed class TgUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("is_bot")] public bool IsBot { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName!
        : !string.IsNullOrWhiteSpace(Username) ? $"@{Username}"
        : Id.ToString();
}

public sealed class TgChat
{
    [JsonPropertyName("id")] public long Id { get; set; }

    /// <summary>
    /// "private", "group", "supergroup" or "channel". Kept as the wire string and
    /// compared by value: an unrecognised type must fall into neither the group nor
    /// the private branch, which a parsed enum with a default member would not give.
    /// </summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>A group's name. Chosen by whoever made the group, so untrusted.</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }

    public bool IsGroup => Type is "group" or "supergroup";

    /// <summary>
    /// Real updates always carry a type; null is what the tests and the /dev
    /// simulator built before groups existed send, and they all mean a private chat.
    /// </summary>
    public bool IsPrivate => Type is null or "private";
}

public sealed class TgPhotoSize
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class TgDocument
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
}
