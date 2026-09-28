namespace EventPhotoBot.State;

/// <summary>
/// The shape of state.json's "settings" object before events existed. Kept only so
/// StateMigration can read it; nothing writes these, because the migration moves
/// every value to its new home and nulls EventState.Legacy in the same step.
/// </summary>
public sealed class LegacySettings
{
    public string EventName { get; set; } = "";
    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;
    public SlideLayout Layout { get; set; } = SlideLayout.Single;
    public bool NewestFirstBoost { get; set; } = true;
    public int RecurringEvery { get; set; } = 10;
    public bool KenBurns { get; set; } = true;
    public bool ShowJoinInvite { get; set; } = true;
    public bool ShowEventName { get; set; } = true;
    public string? TakeoverImageId { get; set; }
    public DateTimeOffset? TakeoverUntil { get; set; }
    public List<LegacySender> Senders { get; set; } = [];
    public List<LegacyGroup> Groups { get; set; } = [];
}

public enum LegacySenderStatus { Known, AutoApprove, Banned }

public sealed class LegacySender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public LegacySenderStatus Status { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}

public sealed class LegacyGroup
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Listening { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}
