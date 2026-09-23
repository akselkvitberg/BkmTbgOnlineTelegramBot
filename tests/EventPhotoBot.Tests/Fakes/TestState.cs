using EventPhotoBot.State;

namespace EventPhotoBot.Tests.Fakes;

public static class TestState
{
    public const string JoinCode = "party2026";

    /// <summary>An empty state as the app has it after startup: migrated, with the default event.</summary>
    public static EventState New()
    {
        var state = new EventState();
        StateMigration.Migrate(state, JoinCode, DateTimeOffset.UtcNow);
        return state;
    }

    /// <summary>Adds a non-default event to a state under test.</summary>
    public static Event AddEvent(this EventState state, string id, string? name = null,
        string? joinCode = null, bool closed = false)
    {
        var ev = new Event
        {
            Id = id,
            Name = name ?? id,
            JoinCode = joinCode ?? $"code-{id}",
            ClosedAt = closed ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        state.Events.Add(ev);
        return ev;
    }
}
