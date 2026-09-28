using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventPhotoBot.State;

public enum ImageSource { Telegram, Admin }
public enum ImageStatus { Pending, Approved, Hidden, Rejected }
public enum PinKind { None, Recurring }
public enum SlideOrder { Shuffle, NewestFirst }

/// <summary>
/// How the screen arranges the photos it is showing at any one moment.
///
/// A closed set rather than a free string: each value is a promise about what the
/// room sees — how many photos are up at once, whether a guest's photo may be
/// cropped to fill its slot, and whether a caption is still legible from the back
/// of the hall. Those are decisions, and they belong here and in the layout table
/// in show.js rather than being rediscovered in a stylesheet.
///
/// <see cref="Single"/> is the original screen and the default: one photo, whole,
/// letterboxed on its own blurred backdrop. It is the only layout that never crops,
/// the only one that animates the slow zoom, and the only one that carries the
/// caption bar — so it stays the safe choice for a room where the photos matter
/// more than the wall does.
///
/// The other five trade that promise for density. <see cref="Mosaic"/>,
/// <see cref="Polaroid"/>, <see cref="Filmstrip"/> and <see cref="Collage"/> crop to
/// fill a cell; an organiser picking one is picking that trade. <see cref="Split"/>
/// does not crop — half a wide screen is still larger than anything a phone took.
/// </summary>
public enum SlideLayout { Single, Mosaic, Polaroid, Filmstrip, Collage, Split }

public sealed class ImageRecord
{
    public required string Id { get; set; }

    /// <summary>
    /// The event the image belongs to — exactly one. Empty only in a state file from
    /// before events existed; StateMigration fills it in on load.
    /// </summary>
    public string EventId { get; set; } = "";

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

    /// <summary>
    /// Where a group photo was posted, kept so the bot can change its reaction when an
    /// organiser decides on it. Null for private chats and admin uploads.
    /// </summary>
    public long? TelegramChatId { get; set; }
    public long? TelegramMessageId { get; set; }
}

/// <summary>
/// A person's place in one event. Having one is what "Known" was, for that event;
/// <see cref="AutoApprove"/> is what the AutoApprove status was.
/// </summary>
public sealed class Membership
{
    public required string EventId { get; set; }

    /// <summary>A pre-approved photographer for this event: photos skip the queue.</summary>
    public bool AutoApprove { get; set; }
}

/// <summary>
/// One person the bot has heard from, across every event. Absence from the roster
/// still means nothing is stored for them and their photos are declined.
/// </summary>
public sealed class Sender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>Global. Messages are dropped silently, nothing is downloaded.</summary>
    public bool Banned { get; set; }

    /// <summary>Where this person's private-chat photos go while that event is open.</summary>
    public string? CurrentEventId { get; set; }

    public List<Membership> Memberships { get; set; } = [];
}

/// <summary>
/// A Telegram group the bot is a member of. The Bot API has no call that lists a
/// bot's groups, so this is the bot's own record, kept from the membership updates
/// Telegram sends when the bot is added or removed (my_chat_member).
///
/// Being in a group is not the same as collecting from it: anyone can add a bot to a
/// group of their own. Photos are collected only once an organiser routes the group
/// to an event. Messages from any other group are dropped without a reply.
/// </summary>
public sealed class BotGroup
{
    /// <summary>Telegram's chat id. Negative for every group.</summary>
    public long Id { get; set; }

    /// <summary>The group's name as last seen. Set by the group's owner, so untrusted.</summary>
    public string Title { get; set; } = "";

    /// <summary>The event members' photos go to. Null: nothing is collected.</summary>
    public string? EventId { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
}

/// <summary>How one event's screen looks and behaves.</summary>
public sealed class EventSettings
{
    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;

    /// <summary>
    /// How the screen arranges what it is showing. Single by default — one whole
    /// photo at a time is the layout that never crops anybody out of their own
    /// picture, and the one every other setting here was written against. The
    /// denser layouts are for a room where photos arrive faster than one every
    /// eight seconds, and nobody watches the screen continuously.
    /// </summary>
    public SlideLayout Layout { get; set; } = SlideLayout.Single;
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

    /// <summary>
    /// Whether the event's name sits small in a corner of the screen while photos
    /// are showing. On by default; the holding card shows the name regardless.
    /// </summary>
    public bool ShowEventName { get; set; } = true;
    public string? TakeoverImageId { get; set; }
    public DateTimeOffset? TakeoverUntil { get; set; }
}

/// <summary>Automatic deletion of an event's old photos. Off unless MaxAgeDays is set.</summary>
public sealed class Retention
{
    public int? MaxAgeDays { get; set; }

    /// <summary>Approved photos kept regardless of age, so the screen always has a pool.</summary>
    public int KeepNewest { get; set; }
}

/// <summary>
/// Something photos are collected for: the church's standing daily screen (the one
/// default event), or a wedding or concert that runs alongside it.
/// </summary>
public sealed class Event
{
    /// <summary>A slug, [a-z0-9-]{1,32}. Screens bookmark it (/show?event=), so it never changes.</summary>
    public required string Id { get; set; }

    /// <summary>Shown on the screen and named in every bot acknowledgement.</summary>
    public string Name { get; set; } = "";

    /// <summary>Exactly one event has this: it never closes and is never deleted.</summary>
    public bool IsDefault { get; set; }

    public required string JoinCode { get; set; }
    public DateTimeOffset? OpensAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }

    /// <summary>Set by an organiser closing the event by hand.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public EventSettings Settings { get; set; } = new();
    public Retention Retention { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EventState
{
    public Dictionary<string, ImageRecord> Images { get; set; } = [];
    public List<Event> Events { get; set; } = [];
    public List<Sender> Senders { get; set; } = [];
    public List<BotGroup> Groups { get; set; } = [];

    /// <summary>
    /// The "settings" object of a state file from before events. Read by
    /// StateMigration and set to null there, so it is never written back.
    /// </summary>
    [JsonPropertyName("settings")]
    public LegacySettings? Legacy { get; set; }
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
