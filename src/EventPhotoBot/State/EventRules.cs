using System.Buffers.Text;
using System.Security.Cryptography;

namespace EventPhotoBot.State;

public enum EventPhase { Scheduled, Open, Closed }

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

    /// <summary>
    /// Computed on every read, never stored: an event closes on its own clock, and a
    /// stored flag would need a write at exactly the moment nobody is making one.
    /// </summary>
    public static bool IsOpen(this Event ev, DateTimeOffset now) =>
        ev.ClosedAt is null
        && (ev.OpensAt is null || ev.OpensAt <= now)
        && (ev.ClosesAt is null || ev.ClosesAt > now);

    public static EventPhase PhaseAt(this Event ev, DateTimeOffset now)
    {
        if (ev.IsOpen(now)) return EventPhase.Open;
        if (ev.ClosedAt is null && ev.OpensAt > now) return EventPhase.Scheduled;
        return EventPhase.Closed;
    }

    /// <summary>A fresh code no event already has. A collision at 128 bits is theoretical; the loop costs nothing.</summary>
    public static string UniqueJoinCode(EventState state)
    {
        while (true)
        {
            var code = GenerateJoinCode();
            if (state.Events.All(e => e.JoinCode != code)) return code;
        }
    }
}
