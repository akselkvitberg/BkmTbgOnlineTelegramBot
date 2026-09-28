using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

/// <summary>
/// The rules for <see cref="EventState.Groups"/>, shared by the update handler (the bot
/// joining or leaving a group) and the admin API (an organiser routing a group to an
/// event, un-routing it, or making the bot leave).
/// </summary>
public static class Groups
{
    /// <summary>
    /// How many groups the bot keeps a row for while they are not routed to an event.
    /// Anyone can add the bot to a group, and each add is a state write nobody at the
    /// event authorised; the cap keeps a stranger adding it to hundreds of groups from
    /// growing state.json without bound. Groups routed to an event are never evicted.
    /// </summary>
    public const int MaxNotListening = 20;

    /// <summary>
    /// Posted in a group each time it is routed to an event. Members of a group shared
    /// photos with each other, not with a screen in a hall; this is the point at which
    /// they learn otherwise, and it names the screen, what is shown and how long it is kept.
    /// </summary>
    public static string NoticeFor(Event ev) =>
        $"Denne gruppen er nå koblet til bildeskjermen for {ev.Name}. Bilder som legges ut her fra nå av, " +
        "kan bli vist på skjermen sammen med navnet til den som la dem ut. En arrangør godkjenner " +
        "bildene før de vises, med unntak av forhåndsgodkjente fotografer. Ikke legg ut bilder her " +
        "som du ikke vil ha på skjermen. " + DeletionRule(ev);

    public static string DeletionRule(Event ev) => ev.Retention.MaxAgeDays switch
    {
        { } days when ev.Retention.KeepNewest > 0 =>
            $"Bildene slettes etter {days} dager, men de {ev.Retention.KeepNewest} nyeste beholdes.",
        { } days => $"Bildene slettes etter {days} dager.",
        null => "Bildene slettes når arrangøren sletter arrangementet.",
    };

    /// <summary>Adds or refreshes the row for a group the bot is in. Never changes EventId.</summary>
    public static BotGroup Remember(EventState state, long id, string? title, DateTimeOffset now)
    {
        var groups = state.Groups;
        var group = groups.FirstOrDefault(g => g.Id == id);
        if (group is null)
        {
            group = new BotGroup { Id = id, FirstSeen = now };
            groups.Add(group);

            var idle = groups.Where(g => g.EventId is null).OrderBy(g => g.FirstSeen).ToList();
            foreach (var evicted in idle.Take(Math.Max(0, idle.Count - MaxNotListening)))
                groups.Remove(evicted);
        }

        if (!string.IsNullOrWhiteSpace(title)) group.Title = title;
        return group;
    }

    /// <summary>
    /// Routes the group to an event, adding the row if the bot joined before it kept
    /// one. True only if this call changed where the group's photos go, so the notice
    /// goes out once per change.
    /// </summary>
    public static bool Route(EventState state, long id, string? title, DateTimeOffset now, string eventId)
    {
        var group = Remember(state, id, title, now);
        if (group.EventId == eventId) return false;
        group.EventId = eventId;
        return true;
    }
}
