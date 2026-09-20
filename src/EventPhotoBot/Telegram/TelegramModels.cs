using System.Text.Json.Serialization;

namespace EventPhotoBot.Telegram;

public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("from")] public TgUser? From { get; set; }
    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("media_group_id")] public string? MediaGroupId { get; set; }
    [JsonPropertyName("photo")] public List<TgPhotoSize>? Photo { get; set; }
    [JsonPropertyName("document")] public TgDocument? Document { get; set; }
}

public sealed class TgUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
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
