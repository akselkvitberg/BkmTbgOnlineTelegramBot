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

/// <summary>
/// What the bot does with a person's photos. Absence from the roster is the
/// fourth case and needs no member: that person has not redeemed the join code,
/// nothing is stored for them, and their photos are declined.
/// </summary>
public enum SenderStatus
{
    /// <summary>Redeemed the join code. Photos go to the approval queue.</summary>
    Known,

    /// <summary>A pre-approved photographer. Photos go straight to the screen.</summary>
    AutoApprove,

    /// <summary>Blocked. Messages are dropped silently, nothing is downloaded.</summary>
    Banned,
}

public sealed class Sender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public SenderStatus Status { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}

public sealed class Settings
{
    /// <summary>
    /// Shown on the slideshow's empty state. Lives here rather than in deploy
    /// configuration because it is the one thing about an event an organiser is
    /// likely to want to fix — a typo, a renamed party — after the screen is
    /// already up, and a redeploy mid-event drops whatever Telegram is holding.
    /// Empty until someone types it in admin; the screen then shows no name.
    /// </summary>
    public string EventName { get; set; } = "";

    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;
    public bool NewestFirstBoost { get; set; } = true;
    public int RecurringEvery { get; set; } = 10;

    /// <summary>
    /// A slow zoom and drift over each slide. On by default: a still photo held for
    /// eight seconds on a large screen reads as a frozen display, and the motion is
    /// what tells a room the screen is live. Off is for a machine whose GPU cannot
    /// keep the animation smooth — a stutter is worse than no movement at all.
    /// </summary>
    public bool KenBurns { get; set; } = true;

    /// <summary>
    /// Whether the screen invites people to send photos — the QR code, the bot
    /// handle, and the wording on the holding card. On by default: that invitation
    /// is how an event gets any photos at all. Off is for a screen the wrong public
    /// can see, a foyer or a street-facing window, where the room should not be
    /// asked to contribute. Visual only: the join link keeps working for anyone who
    /// already has it, and photos already sent keep arriving.
    /// </summary>
    public bool ShowJoinInvite { get; set; } = true;
    public string? TakeoverImageId { get; set; }
    public DateTimeOffset? TakeoverUntil { get; set; }
    public List<Sender> Senders { get; set; } = [];
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
