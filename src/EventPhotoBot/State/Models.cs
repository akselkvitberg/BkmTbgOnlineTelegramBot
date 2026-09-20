using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventPhotoBot.State;

public enum ImageSource { Telegram, Admin }
public enum ImageStatus { Pending, Approved, Hidden, Rejected }
public enum PinKind { None, Recurring }
public enum SlideOrder { Shuffle, NewestFirst }

public sealed class ImageRecord
{
    public required string Id { get; set; }
    public ImageSource Source { get; set; }
    public long? SenderId { get; set; }
    public string? SenderName { get; set; }
    public string? FileUniqueId { get; set; }
    public required string Sha256 { get; set; }
    public string? Caption { get; set; }
    public ImageStatus Status { get; set; }
    public PinKind Pin { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public required string SortKey { get; set; }
    public required string OriginalExtension { get; set; }
}

public sealed class WhitelistEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Trusted { get; set; }
}

public sealed class SeenSender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
}

public sealed class Settings
{
    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;
    public bool NewestFirstBoost { get; set; } = true;
    public int RecurringEvery { get; set; } = 10;
    public string? TakeoverImageId { get; set; }
    public DateTimeOffset? TakeoverUntil { get; set; }
    public bool AutoApproveTrusted { get; set; }
    public List<WhitelistEntry> Whitelist { get; set; } = [];
    public bool PairingMode { get; set; }
    public List<SeenSender> SeenSenders { get; set; } = [];
}

public sealed class EventState
{
    public Dictionary<string, ImageRecord> Images { get; set; } = [];
    public Settings Settings { get; set; } = new();
}

public static class StateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };
}

/// <summary>The only place object names are constructed.</summary>
public static class ObjectPaths
{
    public static string Original(string id, string extension) => $"originals/{id}.{extension}";
    public static string Display(string id) => $"display/{id}.jpg";
    public static string Thumb(string id) => $"thumbs/{id}.jpg";
}
