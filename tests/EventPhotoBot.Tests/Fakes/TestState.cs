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
}
