using System.Buffers.Text;
using System.Security.Cryptography;

namespace EventPhotoBot.State;

/// <summary>Lookups and rules about events, shared by the bot, the API and the sweep.</summary>
public static class EventRules
{
    /// <summary>The standing event. StateMigration guarantees exactly one.</summary>
    public static Event Default(this EventState state) => state.Events.First(e => e.IsDefault);

    public static Event? Find(this EventState state, string? id) =>
        id is null ? null : state.Events.FirstOrDefault(e => e.Id == id);

    /// <summary>The event an image belongs to; the default for a record no event claims.</summary>
    public static Event EventOf(this EventState state, ImageRecord image) =>
        state.Find(image.EventId) ?? state.Default();

    public static Membership? MembershipIn(this Sender sender, string eventId) =>
        sender.Memberships.FirstOrDefault(m => m.EventId == eventId);

    /// <summary>
    /// 128 random bits as base64url: 22 characters, all inside Telegram's deep-link
    /// payload charset [A-Za-z0-9_-] and well under its 64-character limit.
    /// </summary>
    public static string GenerateJoinCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
