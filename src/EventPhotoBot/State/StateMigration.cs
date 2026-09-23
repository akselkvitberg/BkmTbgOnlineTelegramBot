namespace EventPhotoBot.State;

/// <summary>What the migration needs from configuration: the code already on a deployed QR.</summary>
public sealed record StateSeed(string? JoinCode);

/// <summary>
/// Turns a state file from before events into the events shape, in memory. Runs on
/// every load and is idempotent: a state that already has events is left alone.
/// </summary>
public static class StateMigration
{
    public const string DefaultEventId = "daglig";
    public const string DefaultEventName = "Daglig";

    /// <summary>True if anything changed, so the caller knows to persist it.</summary>
    public static bool Migrate(EventState state, string? seedJoinCode, DateTimeOffset now)
    {
        if (state.Events.Count > 0)
        {
            if (state.Legacy is null) return false;
            state.Legacy = null;
            return true;
        }

        var legacy = state.Legacy ?? new LegacySettings();
        var ev = new Event
        {
            Id = DefaultEventId,
            Name = string.IsNullOrWhiteSpace(legacy.EventName) ? DefaultEventName : legacy.EventName,
            IsDefault = true,
            // The code already printed on the church's QR, if there is one: a
            // regenerated code would silently strand everyone who has the old link.
            JoinCode = string.IsNullOrWhiteSpace(seedJoinCode) ? EventRules.GenerateJoinCode() : seedJoinCode,
            Settings = new EventSettings
            {
                SlideSeconds = legacy.SlideSeconds,
                TransitionMs = legacy.TransitionMs,
                Order = legacy.Order,
                Layout = legacy.Layout,
                NewestFirstBoost = legacy.NewestFirstBoost,
                RecurringEvery = legacy.RecurringEvery,
                KenBurns = legacy.KenBurns,
                ShowJoinInvite = legacy.ShowJoinInvite,
                ShowEventName = legacy.ShowEventName,
                TakeoverImageId = legacy.TakeoverImageId,
                TakeoverUntil = legacy.TakeoverUntil,
            },
            // Off: turning retention on is the organiser's decision, not the upgrade's.
            Retention = new Retention(),
            CreatedAt = now,
        };
        state.Events.Add(ev);

        foreach (var image in state.Images.Values)
            if (string.IsNullOrEmpty(image.EventId)) image.EventId = ev.Id;

        foreach (var old in legacy.Senders)
        {
            state.Senders.Add(new Sender
            {
                Id = old.Id,
                Name = old.Name,
                FirstSeen = old.FirstSeen,
                Banned = old.Status == LegacySenderStatus.Banned,
                CurrentEventId = ev.Id,
                // Kept for a banned sender too, so an unban restores what they had.
                Memberships = [new Membership { EventId = ev.Id, AutoApprove = old.Status == LegacySenderStatus.AutoApprove }],
            });
        }

        foreach (var old in legacy.Groups)
        {
            state.Groups.Add(new BotGroup
            {
                Id = old.Id,
                Title = old.Title,
                FirstSeen = old.FirstSeen,
                EventId = old.Listening ? ev.Id : null,
            });
        }

        state.Legacy = null;
        return true;
    }
}
