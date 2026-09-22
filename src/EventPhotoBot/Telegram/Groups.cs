using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

/// <summary>
/// The rules for <see cref="Settings.Groups"/>, shared by the update handler (the bot
/// joining, leaving, or being given the join code) and the admin API (an organiser
/// turning listening on or off, or making the bot leave).
/// </summary>
public static class Groups
{
    /// <summary>
    /// How many groups the bot keeps a row for while not listening to them. Anyone
    /// can add the bot to a group, and each add is a state write nobody at the event
    /// authorised; the cap keeps a stranger adding it to hundreds of groups from
    /// growing state.json without bound. Listening groups are never evicted.
    /// </summary>
    public const int MaxNotListening = 20;

    /// <summary>
    /// Posted once in a group when the bot starts collecting from it. Members of a
    /// group shared photos with each other, not with a screen in a hall; this is
    /// the point at which they learn otherwise, and it names what is shown — the
    /// photo and the name of whoever posted it.
    /// </summary>
    public const string ListeningNotice =
        "Denne gruppen er nå koblet til bildeskjermen på arrangementet. Bilder som legges " +
        "ut her fra nå av, kan bli vist på skjermen sammen med navnet til den som la dem ut. " +
        "En arrangør godkjenner bildene før de vises, med unntak av forhåndsgodkjente " +
        "fotografer. Ikke legg ut bilder her som du ikke vil ha på skjermen. Alt slettes " +
        "etter arrangementet.";

    /// <summary>Adds or refreshes the row for a group the bot is in. Never changes Listening.</summary>
    public static BotGroup Remember(EventState state, long id, string? title, DateTimeOffset now)
    {
        var groups = state.Settings.Groups;
        var group = groups.FirstOrDefault(g => g.Id == id);
        if (group is null)
        {
            group = new BotGroup { Id = id, FirstSeen = now };
            groups.Add(group);

            var idle = groups.Where(g => !g.Listening).OrderBy(g => g.FirstSeen).ToList();
            foreach (var evicted in idle.Take(Math.Max(0, idle.Count - MaxNotListening)))
                groups.Remove(evicted);
        }

        if (!string.IsNullOrWhiteSpace(title)) group.Title = title;
        return group;
    }

    /// <summary>
    /// Turns listening on, adding the row if the bot joined before it kept one.
    /// True only if this call is what turned it on, so the notice goes out once.
    /// </summary>
    public static bool Listen(EventState state, long id, string? title, DateTimeOffset now)
    {
        var group = Remember(state, id, title, now);
        if (group.Listening) return false;
        group.Listening = true;
        return true;
    }
}
