# Multiple Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the event from the deployment into data: a standing default event (Daglig) plus special events that run alongside it, each with its own join code, screen, settings, photos and retention.

**Architecture:** One `state.json` as today, reshaped so `Events`, `Senders` and `Groups` sit at the top level and every `ImageRecord` carries an `EventId`. A pre-events file is migrated on load. The bot routes a private photo by the sender's current event (falling back to the default event only for its members) and a group photo by the event an admin routed the group to. Every existing admin endpoint takes `?event=`, defaulting to the default event, so the church screen's URL keeps working.

**Tech Stack:** .NET 10 minimal APIs, System.Text.Json, xUnit 2.9 with `WebApplicationFactory`, plain HTML/JS admin pages, Terraform + PowerShell deploy on Cloud Run and GCS.

**Spec:** [`docs/superpowers/specs/2026-09-23-multiple-events-design.md`](../specs/2026-09-23-multiple-events-design.md)

## Global Constraints

- No new NuGet packages. Target stays `net10.0`.
- Every user-facing string is Norwegian (bokmål), written inline, as today. Code comments are English and match the surrounding comment density.
- Enums are read off the wire only through `ApiEndpoints.TryParseName`.
- State changes only through `StateStore.MutateAsync`. Ingest writes objects first, state last. Every delete writes state first, objects last.
- Every image has exactly one `EventId`.
- Default event: exactly one, `IsDefault = true`, id `daglig`, never closes, never deleted. Its name is the legacy `EventName`, or `Daglig` when that was empty.
- Slugs: `[a-z0-9-]{1,32}`, immutable.
- Join codes: at least 128 bits of randomness, encoded in `[A-Za-z0-9_-]`, within Telegram's 64-character deep-link payload limit, unique across events. Matching is constant-time, as `JoinCodeMatches` does today.
- `IsOpen(now) = ClosedAt is null && (OpensAt is null || OpensAt <= now) && (ClosesAt is null || ClosesAt > now)`. Computed on every read, never stored.
- Times are stored in UTC and entered and shown in Europe/Oslo time.
- `callback_data = "ev:{eventId}"`.
- Reactions: Pending 👀, Approved (manual or auto) 🔥, Rejected/hidden/deleted: none (empty reaction list). Best effort: a failure is logged and the decision stands.
- Private-chat replies drop the sentence "Alt slettes etter arrangementet". The group notice names the event and states its deletion rule.
- Retention keeps: images received within `MaxAgeDays`, the `KeepNewest` newest approved images, pinned images, and the current takeover image.
- Out of scope: accounts and roles, more than one instance, a database, reactions in private chats, guest web upload, splitting `state.json`.
- Commit after every task, message style `feat(scope): …` / `fix(scope): …`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- The full suite (`dotnet test` from the repo root) passes at the end of every task. Baseline before Task 1: 266 passing.

### Decisions made while planning (the spec is amended in Task 14)

- **The bucket's 30-day lifecycle rule is removed** (`infra/main.tf:100-106`). It deletes image bytes on age alone, which contradicts "kept until an admin deletes the event" and would leave the default event's `KeepNewest` pool pointing at deleted files.
- **Reactions are not changed on bulk deletes** (delete-all, deleting an event, the retention sweep). They are changed on single decisions, single deletes, takeover approval and the ban cascade. Hundreds of `setMessageReaction` calls inside one request risk Telegram's rate limit and the request timeout.
- **Event endpoints split by what they replace:** `PATCH /api/events/{id}` sets the name only. `PUT /api/events/{id}/schedule` and `PUT /api/events/{id}/retention` replace those groups of fields, so "clear this date" needs no sentinel values.
- **Group routing body:** `{"eventId": "<id>"}` routes, `{"eventId": ""}` un-routes, and a missing or null `eventId` is a 400, so a malformed body can never un-route a group.
- **A code for a scheduled event** gets the reply "{navn} har ikke startet ennå." The spec only covered closed events.
- **The QR for a scheduled event is served,** so it can be printed in advance. It is refused only once the event is closed.
- **An event name cannot be empty,** because the bot says it in every acknowledgement. The existing "event name can be cleared" behaviour goes away.
- **Cloud Run request timeout rises from 120 s to 900 s,** so a ZIP export of a large event can finish. Firebase Hosting cuts a request at 60 s, so large exports must use the run.app URL. This is documented in Task 14.
- **Export file names use UTC:** `{ReceivedAt:yyyyMMdd-HHmmss}Z-{id}.{ext}`. The container may not carry tz data for Europe/Oslo.

## Review Focus

1. **A pre-events `state.json` with an active takeover.** A reasonable person expects the screen to keep showing the takeover photo after the upgrade. The migration must carry `TakeoverImageId` and `TakeoverUntil` onto the default event. Pinned in Task 1.
2. **An event that closes on its own clock while its screen is open.** No state write happens at `ClosesAt`, so the generation does not move. The manifest ETag must still change, or the screen keeps inviting guests to a closed event. Pinned in Task 3.
3. **A sender whose current event has been deleted sends a photo.** It should go to the default event if they are a member, not crash on a null lookup. Pinned in Task 4.
4. **A group routed to an event that is then deleted.** The next photo in that group must be ignored silently, not throw. Pinned in Task 6.
5. **Reopening an event whose end time has passed.** Reopen must actually reopen it, by clearing `ClosesAt`, rather than leaving it closed by time. Pinned in Task 2.

---

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/EventPhotoBot/State/LegacyModels.cs` | The pre-events `settings` shape, read only by the migration |
| `src/EventPhotoBot/State/EventRules.cs` | Lookups and rules: default event, find, membership, open/phase, join codes |
| `src/EventPhotoBot/State/StateMigration.cs` | Pre-events file → events shape, idempotent; `StateSeed` |
| `src/EventPhotoBot/State/ImageObjects.cs` | Deleting an image's three objects, used by every delete path |
| `src/EventPhotoBot/State/RetentionSweep.cs` | Which images retention deletes, and running it |
| `src/EventPhotoBot/SecretComparison.cs` | The constant-time secret check, moved out of `Program.cs` and `UpdateHandler` |
| `src/EventPhotoBot/Telegram/Routing.cs` | Where a private photo goes; which switch buttons a sender gets |
| `src/EventPhotoBot/Telegram/Reactions.cs` | Status → reaction, and syncing a group photo's reaction |
| `src/EventPhotoBot/Web/EventEndpoints.cs` | `/api/events…`, including export |
| `src/EventPhotoBot/Web/EventScope.cs` | Resolving `?event=` |
| `src/EventPhotoBot/Web/RetentionEndpoints.cs` | `/internal/retention` and the admin "Rydd nå" |
| `src/EventPhotoBot/wwwroot/admin/events.html` | The events page |
| `tests/EventPhotoBot.Tests/Fakes/TestState.cs` | A migrated empty state; adding events in tests |
| `tests/EventPhotoBot.Tests/StateMigrationTests.cs`, `EventRulesTests.cs`, `EventApiTests.cs`, `ScopedApiTests.cs`, `RoutingTests.cs`, `SwitchButtonTests.cs`, `TelegramClientPayloadTests.cs`, `ReactionTests.cs`, `RetentionSweepTests.cs`, `RetentionEndpointTests.cs`, `ExportTests.cs` | Tests for the above |

**Modified:** `Models.cs`, `StateStore.cs`, `AppConfig.cs`, `Program.cs`, `BotIdentity.cs`, `QrEndpoint.cs`, `Groups.cs`, `UpdateHandler.cs`, `TelegramModels.cs`, `ITelegramClient.cs`, `TelegramClient.cs`, `OfflineTelegramClient.cs`, `ApiEndpoints.cs`, `ManifestBuilder.cs`, `DevEndpoints.cs`, `AuthEndpoints.cs`, every admin page, `admin.js`, `admin.css`, `show.js`, `dev.html`, `infra/main.tf`, `infra/deploy.ps1`, `README.md`, `docs/RUNBOOK.md`, the spec, and the existing tests named in each task.

---

### Task 1: Reshape the state model and migrate on load

Behaviour is unchanged by this task: there is one event, the default one, and everything that used `Settings` uses it. The later tasks add the second event.

**Files:**
- Create: `src/EventPhotoBot/State/LegacyModels.cs`, `src/EventPhotoBot/State/EventRules.cs`, `src/EventPhotoBot/State/StateMigration.cs`, `src/EventPhotoBot/SecretComparison.cs`, `tests/EventPhotoBot.Tests/Fakes/TestState.cs`, `tests/EventPhotoBot.Tests/StateMigrationTests.cs`
- Modify: `src/EventPhotoBot/State/Models.cs`, `src/EventPhotoBot/State/StateStore.cs`, `src/EventPhotoBot/AppConfig.cs`, `src/EventPhotoBot/Program.cs`, `src/EventPhotoBot/Telegram/BotIdentity.cs`, `src/EventPhotoBot/Web/QrEndpoint.cs`, `src/EventPhotoBot/Telegram/Groups.cs`, `src/EventPhotoBot/Telegram/UpdateHandler.cs`, `src/EventPhotoBot/Web/ApiEndpoints.cs`, `src/EventPhotoBot/Web/ManifestBuilder.cs`, `src/EventPhotoBot/Web/DevEndpoints.cs`
- Modify tests: `StateModelTests.cs`, `StateStoreTests.cs`, `AppConfigTests.cs`, `BotIdentityTests.cs`, `ManifestBuilderTests.cs`, `ManifestEndpointTests.cs`, `AdminApiTests.cs`, `SenderApiTests.cs`, `TakeoverInvariantTests.cs`, `DeleteAllImagesTests.cs`, `UpdateHandlerTests.cs`

**Interfaces:**
- Produces (used by every later task):
  - `Event`, `EventSettings`, `Retention`, `Sender { Banned, CurrentEventId, Memberships }`, `Membership { EventId, AutoApprove }`, `BotGroup { EventId }`, `ImageRecord { EventId, TelegramChatId, TelegramMessageId }`, `EventState { Images, Events, Senders, Groups, Legacy }`
  - `EventRules.Default(this EventState) → Event`, `EventRules.Find(this EventState, string?) → Event?`, `EventRules.EventOf(this EventState, ImageRecord) → Event`, `EventRules.MembershipIn(this Sender, string) → Membership?`, `EventRules.GenerateJoinCode() → string`
  - `StateMigration.DefaultEventId = "daglig"`, `StateMigration.DefaultEventName = "Daglig"`, `StateMigration.Migrate(EventState, string? seedJoinCode, DateTimeOffset now) → bool`, `record StateSeed(string? JoinCode)`
  - `StateStore.InitializeAsync(CancellationToken)`, `StateStore.MigratedOnLoad`
  - `SecretComparison.Matches(string expected, string? supplied) → bool`
  - `BotIdentity.JoinUrlFor(string joinCode) → string?` (replaces `JoinUrl`)
  - `ManifestBuilder.Build(EventState state, Event ev, long generation, DateTimeOffset now, string? joinUrl = null)`
  - `Groups.Route(EventState, long id, string? title, DateTimeOffset now, string eventId) → bool` (replaces `Listen`)
  - Test helper `TestState.New() → EventState` and `TestState.JoinCode = "party2026"`

- [ ] **Step 1: Write the migration tests**

Create `tests/EventPhotoBot.Tests/Fakes/TestState.cs`:

```csharp
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
```

Create `tests/EventPhotoBot.Tests/StateMigrationTests.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class StateMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static EventState Load(string json) =>
        JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

    [Fact]
    public void A_fresh_state_gets_one_default_event_with_the_seeded_code()
    {
        var state = new EventState();

        Assert.True(StateMigration.Migrate(state, "party2026", Now));

        var ev = Assert.Single(state.Events);
        Assert.True(ev.IsDefault);
        Assert.Equal(StateMigration.DefaultEventId, ev.Id);
        Assert.Equal(StateMigration.DefaultEventName, ev.Name);
        Assert.Equal("party2026", ev.JoinCode);
        Assert.Null(ev.Retention.MaxAgeDays);
    }

    [Fact]
    public void Without_a_seed_the_default_event_gets_a_generated_deep_link_safe_code()
    {
        var state = new EventState();

        StateMigration.Migrate(state, null, Now);

        Assert.Matches(new Regex("^[A-Za-z0-9_-]{22}$"), state.Default().JoinCode);
    }

    [Fact]
    public void A_pre_events_file_moves_every_value_to_the_default_event()
    {
        const string json = """
            {"images":{"01A":{"id":"01A","sha256":"x","sortKey":"01A","originalExtension":"jpg","status":"approved"}},
             "settings":{"eventName":"Sommerfest","slideSeconds":12,"layout":"mosaic","showJoinInvite":false,
                         "takeoverImageId":"01A","takeoverUntil":"2026-09-23T13:00:00+00:00",
                         "senders":[{"id":1,"name":"Ada","status":"known"},
                                    {"id":2,"name":"Bo","status":"autoApprove"},
                                    {"id":3,"name":"Cy","status":"banned"}],
                         "groups":[{"id":-10,"title":"On","listening":true},
                                   {"id":-11,"title":"Off","listening":false}]}}
            """;
        var state = Load(json);

        Assert.True(StateMigration.Migrate(state, "party2026", Now));

        var ev = state.Default();
        Assert.Equal("Sommerfest", ev.Name);
        Assert.Equal(12, ev.Settings.SlideSeconds);
        Assert.Equal(SlideLayout.Mosaic, ev.Settings.Layout);
        Assert.False(ev.Settings.ShowJoinInvite);
        // Review focus 1: a takeover running across the upgrade keeps the screen.
        Assert.Equal("01A", ev.Settings.TakeoverImageId);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero), ev.Settings.TakeoverUntil);

        Assert.Equal(ev.Id, state.Images["01A"].EventId);

        var ada = state.Senders.Single(s => s.Id == 1);
        Assert.False(ada.Banned);
        Assert.False(ada.MembershipIn(ev.Id)!.AutoApprove);
        Assert.Equal(ev.Id, ada.CurrentEventId);
        Assert.True(state.Senders.Single(s => s.Id == 2).MembershipIn(ev.Id)!.AutoApprove);
        var cy = state.Senders.Single(s => s.Id == 3);
        Assert.True(cy.Banned);
        Assert.NotNull(cy.MembershipIn(ev.Id));   // an unban restores it

        Assert.Equal(ev.Id, state.Groups.Single(g => g.Id == -10).EventId);
        Assert.Null(state.Groups.Single(g => g.Id == -11).EventId);

        Assert.Null(state.Legacy);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(state, StateJson.Options));
        Assert.False(written.RootElement.TryGetProperty("settings", out _));
    }

    [Fact]
    public void A_file_from_before_groups_migrates_with_no_groups()
    {
        var state = Load("""{"images":{},"settings":{"senders":[{"id":42,"name":"Ada","status":"known"}]}}""");

        StateMigration.Migrate(state, "party2026", Now);

        Assert.Empty(state.Groups);
        Assert.Single(state.Senders);
    }

    [Fact]
    public void An_empty_legacy_name_becomes_daglig()
    {
        var state = Load("""{"images":{},"settings":{"eventName":""}}""");

        StateMigration.Migrate(state, "party2026", Now);

        Assert.Equal("Daglig", state.Default().Name);
    }

    [Fact]
    public void Migrating_a_migrated_state_changes_nothing()
    {
        var state = new EventState();
        StateMigration.Migrate(state, "party2026", Now);
        var before = JsonSerializer.Serialize(state, StateJson.Options);

        Assert.False(StateMigration.Migrate(state, "other", Now.AddDays(1)));
        Assert.Equal(before, JsonSerializer.Serialize(state, StateJson.Options));
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StateMigrationTests"`
Expected: build FAILS with "The type or namespace name 'StateMigration' could not be found" (and `Default`, `Legacy`, `Senders` on `EventState`).

- [ ] **Step 3: Reshape `Models.cs`**

In `src/EventPhotoBot/State/Models.cs`, keep the enums at the top (`ImageSource`, `ImageStatus`, `PinKind`, `SlideOrder`, `SlideLayout` with its comment) and `StateJson` and `ObjectPaths` at the bottom unchanged. Delete `SenderStatus`, the old `Sender`, the old `BotGroup`, the old `Settings` and the old `EventState`. The file between the enums and `StateJson` becomes:

```csharp
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
```

Then `EventSettings`. It is today's `Settings` with `EventName`, `Senders` and `Groups` removed. Move the doc comments from those fields in the old `Settings` unchanged:

```csharp
/// <summary>How one event's screen looks and behaves.</summary>
public sealed class EventSettings
{
    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;

    /// (keep the existing <summary> for Layout)
    public SlideLayout Layout { get; set; } = SlideLayout.Single;
    public bool NewestFirstBoost { get; set; } = true;
    public int RecurringEvery { get; set; } = 10;

    /// (keep the existing <summary> for KenBurns)
    public bool KenBurns { get; set; } = true;

    /// (keep the existing <summary> for ShowJoinInvite)
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
```

(`Models.cs` already has `using System.Text.Json.Serialization;`.)

- [ ] **Step 4: Add the legacy shape, the rules and the migration**

Create `src/EventPhotoBot/State/LegacyModels.cs`:

```csharp
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
```

Create `src/EventPhotoBot/SecretComparison.cs` by moving the body of `SecretTokenMatches` from `Program.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace EventPhotoBot;

public static class SecretComparison
{
    /// <summary>
    /// Constant-time over the UTF-8 bytes, hashing both sides before comparing:
    /// CryptographicOperations.FixedTimeEquals alone still leaks length through its
    /// own argument check unless both inputs are already the same size, and hashing
    /// first fixes that at 32 bytes regardless of what was supplied — a missing value
    /// (null) hashes and compares exactly like a present-but-wrong one. Used for every
    /// secret a stranger can reach: the webhook secret, join codes, the retention secret.
    /// </summary>
    public static bool Matches(string expected, string? supplied)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
```

Create `src/EventPhotoBot/State/EventRules.cs`:

```csharp
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
```

Create `src/EventPhotoBot/State/StateMigration.cs`:

```csharp
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
```

- [ ] **Step 5: Run the migration tests**

Run: `dotnet test --filter "FullyQualifiedName~StateMigrationTests"`
Expected: still FAILS to build, now because the rest of `src/` references the removed `Settings`. Steps 6–9 fix that.

- [ ] **Step 6: Migrate on load in `StateStore`**

In `src/EventPhotoBot/State/StateStore.cs`:

1. Change the class declaration to take the seed:

```csharp
public sealed class StateStore(IObjectStore objects, ILogger<StateStore>? logger = null, StateSeed? seed = null)
```

2. Add the property below `Generation`:

```csharp
    /// <summary>
    /// Whether the last load changed the state it read (a file from before events, or
    /// no file at all). InitializeAsync persists it, so a generated join code is fixed
    /// before anyone scans it.
    /// </summary>
    public bool MigratedOnLoad { get; private set; }
```

3. Replace `LoadAsync` with:

```csharp
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var stored = await ReadStateWithRetryAsync(ct);
        if (stored is null)
        {
            _state = new EventState();
            _lastBytes = [];
            _generation = 0;
            logger?.LogInformation("No existing state found; starting from defaults.");
        }
        else
        {
            _state = JsonSerializer.Deserialize<EventState>(stored.Bytes, StateJson.Options)
                     ?? new EventState();
            _lastBytes = stored.Bytes;
            _generation = stored.Generation;
            logger?.LogInformation("Loaded state at generation {Generation} with {Count} images.",
                _generation, _state.Images.Count);
        }

        MigratedOnLoad = StateMigration.Migrate(_state, seed?.JoinCode, DateTimeOffset.UtcNow);
        if (MigratedOnLoad) logger?.LogInformation("Migrated state to the events shape.");
    }

    /// <summary>Startup's load: LoadAsync, then one write if the load migrated anything.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        if (MigratedOnLoad) await MutateAsync(_ => { }, ct);
    }
```

4. In `MutateAsync`, directly after `_lastBytes = bytes;` add:

```csharp
                    // One line per write, so the point where one file stops being
                    // enough is visible in the logs before it is felt in admin.
                    logger?.LogInformation("Wrote state.json at generation {Generation}: {Bytes} bytes.",
                        _generation, bytes.Length);
```

5. Replace `ClearExpiredTakeover` with:

```csharp
    private static void ClearExpiredTakeover(EventState state)
    {
        foreach (var ev in state.Events)
        {
            var s = ev.Settings;
            if (s.TakeoverImageId is null) continue;
            if (s.TakeoverUntil is { } until && until <= DateTimeOffset.UtcNow)
            {
                s.TakeoverImageId = null;
                s.TakeoverUntil = null;
            }
        }
    }
```

- [ ] **Step 7: Make `JOIN_CODE` optional and wire the seed**

In `src/EventPhotoBot/AppConfig.cs`:

- Replace `public required string JoinCode { get; init; }` with:

```csharp
    /// <summary>
    /// Optional. Read once, by StateMigration, to give the default event the code
    /// already printed on the QR of a deployment from before events. After that every
    /// event's code lives in state and is rotated from admin.
    /// </summary>
    public string? JoinCode { get; init; }
```

- Remove `"JOIN_CODE"` from `SecretKeys`.
- Replace the join-code block in `Load` with:

```csharp
        var joinCode = config["JOIN_CODE"]?.Trim();
        if (string.IsNullOrEmpty(joinCode))
        {
            joinCode = null;
        }
        else if (!JoinCodePattern.IsMatch(joinCode))
        {
            throw new InvalidOperationException(
                "JOIN_CODE must be 1-64 characters from A-Z, a-z, 0-9, underscore or hyphen " +
                "(Telegram's deep-link payload charset). Fix the secret version and redeploy.");
        }
```

- In `LogLoaded`, after the `foreach`, add `if (JoinCode is not null) logger.LogInformation("Secret {Key} loaded.", "JOIN_CODE");`.

In `src/EventPhotoBot/Program.cs`:

- After `builder.Services.AddSingleton(config);` add `builder.Services.AddSingleton(new StateSeed(config.JoinCode));`.
- Replace `await app.Services.GetRequiredService<StateStore>().LoadAsync();` with `await app.Services.GetRequiredService<StateStore>().InitializeAsync();`, and update the comment above it to: `// Load state once, at startup — the only read of state.json — and persist any migration.`
- Replace `SecretTokenMatches(config.WebhookSecret, …)` with `SecretComparison.Matches(config.WebhookSecret, http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"])`. Delete the local `SecretTokenMatches` function and its doc comment (now on `SecretComparison`), and remove the `using System.Security.Cryptography;` and `using System.Text;` lines if nothing else uses them.

In `src/EventPhotoBot/Telegram/BotIdentity.cs`, drop the `AppConfig` dependency and replace `JoinUrl`:

```csharp
public sealed class BotIdentity
{
    public string? Username { get; private set; }

    /// <summary>The deep link for one event's code; null while the username is unknown.</summary>
    public string? JoinUrlFor(string joinCode) =>
        Username is null ? null : $"https://t.me/{Username}?start={joinCode}";
```

(`ResolveAsync` and `Forget` stay as they are.)

In `src/EventPhotoBot/Web/QrEndpoint.cs`:

```csharp
        app.MapGet("/api/join-qr.svg", (StateStore store, BotIdentity identity) =>
        {
            if (identity.JoinUrlFor(store.Snapshot.Default().JoinCode) is not { } url) return Results.NotFound();
```

(add `using EventPhotoBot.State;`).

- [ ] **Step 8: Move the bot and the groups onto the new model**

`src/EventPhotoBot/Telegram/Groups.cs`: replace `state.Settings.Groups` with `state.Groups` and `!g.Listening` with `g.EventId is null`. Update the `MaxNotListening` summary's "while not listening to them" to "while they are not routed to an event". Replace `Listen` with:

```csharp
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
```

`src/EventPhotoBot/Telegram/UpdateHandler.cs`:

1. Remove the `AppConfig config` constructor parameter.
2. Delete `JoinCodeMatches`. Its two callers use `SecretComparison.Matches(expected, supplied)`.
3. In `HandleAsync`, replace from `var entry = …` through the `if (entry is null) { … }` block with:

```csharp
        var entry = store.Snapshot.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, and before anything that costs a download, a reply or a
        // write. A banned sender gets no signal at all — a reply would both confirm
        // the ban landed and make the bot a reply relay for whoever earned it.
        if (entry is { Banned: true }) return;

        if (entry?.MembershipIn(store.Snapshot.Default().Id) is null)
        {
            await HandleUnredeemedAsync(message, sender, chat, ct);
            return;
        }
```

4. In `HandleMembershipAsync`, replace `store.Snapshot.Settings.Groups` and `state.Settings.Groups` with `store.Snapshot.Groups` and `state.Groups`.
5. In `HandleGroupMessageAsync`, replace the lookup and the not-listening block with:

```csharp
        var entry = store.Snapshot.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, as in a private chat, and before the join code: a banned
        // person must not be able to open a group of their own to the queue.
        if (entry is { Banned: true }) return;

        var group = store.Snapshot.Groups.FirstOrDefault(g => g.Id == chat.Id);
        if (group?.EventId is null)
        {
            if (message.Text is { } text && TryReadGroupJoinCode(text, out var supplied)
                && SecretComparison.Matches(store.Snapshot.Default().JoinCode, supplied))
                await StartListeningAsync(chat, ct);
            return;
        }
```

6. `StartListeningAsync`: `Groups.Route(state, chat.Id, chat.Title, DateTimeOffset.UtcNow, state.Default().Id)`.
7. `MigrateGroupAsync`: `state.Settings.Groups` → `state.Groups`; `existing.Listening |= old.Listening;` → `existing.EventId ??= old.EventId;`.
8. Replace `HandleUnredeemedAsync`'s redemption block with:

```csharp
        if (message.Text is { } text && TryReadJoinCode(text, out var supplied)
            && SecretComparison.Matches(store.Snapshot.Default().JoinCode, supplied))
        {
            var joined = await store.MutateAsync(state =>
            {
                // Re-checked under the store's lock: two /start messages racing must
                // not produce two rows, and a ban landing meanwhile must win.
                var eventId = state.Default().Id;
                var row = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (row is null)
                {
                    row = new Sender { Id = sender.Id, Name = sender.DisplayName, FirstSeen = DateTimeOffset.UtcNow };
                    state.Senders.Add(row);
                }
                if (row.Banned) return false;
                if (row.MembershipIn(eventId) is null) row.Memberships.Add(new Membership { EventId = eventId });
                row.CurrentEventId ??= eventId;
                return true;
            }, ct);
            if (!joined) return;

            // (keep the existing comment and reply below unchanged)
```

9. In `IngestAsync`, replace the status block inside `MutateAsync` (from `var now = …` through `status = member.Status; }` and `approved = status == SenderStatus.AutoApprove;`) with:

```csharp
            var now = DateTimeOffset.UtcNow;
            string eventId;
            if (chat.IsGroup)
            {
                // Re-read under the lock, unlike the private path's entry: a group
                // member was never asked to join, so an admin removing the group or
                // banning the member during the download must win. A ban that lost
                // this race would leave a photo the ban cascade has already swept past.
                var group = state.Groups.FirstOrDefault(g => g.Id == chat.Id);
                if (group?.EventId is not { } routed) return IngestOutcome.Refused;

                var member = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (member is null)
                {
                    // Posting a photo in a group the bot collects from is this member's
                    // join. The group was told so by the notice when collecting started.
                    member = new Sender { Id = sender.Id, Name = sender.DisplayName, FirstSeen = now };
                    state.Senders.Add(member);
                }
                if (member.Banned) return IngestOutcome.Refused;

                var membership = member.MembershipIn(routed);
                if (membership is null)
                {
                    membership = new Membership { EventId = routed };
                    member.Memberships.Add(membership);
                }

                // Free: this write happens anyway, and it keeps a renamed group
                // recognisable on the admin page.
                if (!string.IsNullOrWhiteSpace(chat.Title)) group.Title = chat.Title;
                eventId = routed;
                approved = membership.AutoApprove;
            }
            else
            {
                eventId = state.Default().Id;
                approved = entry?.MembershipIn(eventId)?.AutoApprove == true;
            }
```

and add `EventId = eventId,` to the `new ImageRecord { … }` initializer. Delete the now-unused `var status = entry?.Status;`.

- [ ] **Step 9: Move the API, manifest and dev tools onto the default event**

`src/EventPhotoBot/Web/ManifestBuilder.cs`:

- Change the signature to `public static Manifest Build(EventState state, Event ev, long generation, DateTimeOffset now, string? joinUrl = null)`.
- `var settings = ev.Settings;`
- `approved` becomes `state.Images.Values.Where(i => i.EventId == ev.Id && i.Status == ImageStatus.Approved)`.
- `settings.EventName` → `ev.Name`.
- `PendingCount: state.Images.Values.Count(i => i.EventId == ev.Id && i.Status == ImageStatus.Pending)`.
- `ActiveTakeover(state, ev, now)` with `var settings = ev.Settings;` inside.

`src/EventPhotoBot/Web/ApiEndpoints.cs`:

- Delete `SenderStatusRequest`'s use of `SenderStatus`. Keep the record and add, inside `ApiEndpoints`, `private enum SenderStatusInput { Known, AutoApprove, Banned }`. It exists only until Task 8 replaces the route.
- `GET /api/settings`:

```csharp
        app.MapGet("/api/settings", (StateStore store) =>
        {
            var state = store.Snapshot;
            var ev = state.Default();
            var s = ev.Settings;
            return Results.Ok(new
            {
                EventName = ev.Name,
                s.SlideSeconds,
                s.TransitionMs,
                Order = s.Order == SlideOrder.NewestFirst ? "newest-first" : "shuffle",
                s.NewestFirstBoost,
                s.RecurringEvery,
                s.KenBurns,
                s.ShowJoinInvite,
                s.ShowEventName,
                Layout = s.Layout.ToString().ToLowerInvariant(),
                s.TakeoverImageId,
                s.TakeoverUntil,
                Senders = state.Senders.Select(sender => new
                {
                    sender.Id,
                    sender.Name,
                    sender.FirstSeen,
                    Status = sender.Banned ? "banned"
                        : sender.MembershipIn(ev.Id)?.AutoApprove == true ? "autoApprove" : "known",
                }),
                Groups = state.Groups.Select(group => new
                {
                    group.Id,
                    group.Title,
                    Listening = group.EventId is not null,
                    group.FirstSeen,
                }),
            });
        });
```

- `POST /api/groups/{id}/listening`: `state.Settings.Groups` → `state.Groups`. `group.Listening = false` → `group.EventId = null`. `Groups.Listen(state, id, null, DateTimeOffset.UtcNow)` → `Groups.Route(state, id, null, DateTimeOffset.UtcNow, state.Default().Id)`. `store.Snapshot.Settings.Groups` in the leave route → `store.Snapshot.Groups`, and `state.Settings.Groups.RemoveAll` → `state.Groups.RemoveAll`.
- `GET /api/manifest`: `var state = store.Snapshot; var ev = state.Default();` then `ManifestBuilder.Build(state, ev, store.Generation, DateTimeOffset.UtcNow, identity.JoinUrlFor(ev.JoinCode))`. Update the comment above: the join URL depends only on the event's code, which changes only with a state write.
- `PUT /api/takeover`: after the image lookup, `var settings = state.EventOf(image).Settings;` and set `settings.TakeoverImageId` / `settings.TakeoverUntil`.
- `DELETE /api/takeover`: operate on `store.Snapshot.Default().Settings` / `state.Default().Settings`.
- `DELETE /api/images` (all): only the default event's images, same single write:

```csharp
                var eventId = store.Snapshot.Default().Id;
                if (!store.Snapshot.Images.Values.Any(i => i.EventId == eventId)) return Results.Ok(new { deleted = 0 });

                var removed = await store.MutateAsync(state =>
                {
                    var images = state.Images.Values.Where(i => i.EventId == eventId).ToList();
                    foreach (var image in images) state.Images.Remove(image.Id);
                    var settings = state.Default().Settings;
                    settings.TakeoverImageId = null;
                    settings.TakeoverUntil = null;
                    return images;
                });
```

- `POST /api/images`: add `EventId = state.Default().Id,` by making the write `await store.MutateAsync(state => state.Images[id] = new ImageRecord { Id = id, EventId = state.Default().Id, … })`.
- `PATCH /api/settings`: `var ev = state.Default(); var s = ev.Settings;` and the event-name branch writes `ev.Name`.
- `POST /api/senders/{id}/status`:

```csharp
                if (!TryParseName<SenderStatusInput>(request.Status, out var status))
                    return Results.BadRequest(
                        new { error = "status må være known, autoApprove eller banned." });

                return await store.MutateAsync(state =>
                {
                    var eventId = state.Default().Id;
                    var sender = state.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // Creating on write is how an organiser pre-approves a
                        // photographer, or pre-bans a nuisance, before that person has
                        // ever messaged the bot.
                        sender = new Sender { Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow };
                        state.Senders.Add(sender);
                    }

                    sender.Banned = status == SenderStatusInput.Banned;
                    if (!sender.Banned)
                    {
                        var membership = sender.MembershipIn(eventId);
                        if (membership is null)
                        {
                            membership = new Membership { EventId = eventId };
                            sender.Memberships.Add(membership);
                        }
                        membership.AutoApprove = status == SenderStatusInput.AutoApprove;
                    }

                    // (keep the existing ban cascade below, unchanged except that it
                    // tests `sender.Banned` instead of `status == SenderStatus.Banned`)
```

- `ClearTakeoverIfHeldBy` covers every event:

```csharp
    private static void ClearTakeoverIfHeldBy(EventState state, string id)
    {
        foreach (var ev in state.Events)
        {
            if (ev.Settings.TakeoverImageId != id) continue;
            ev.Settings.TakeoverImageId = null;
            ev.Settings.TakeoverUntil = null;
        }
    }
```

`src/EventPhotoBot/Web/DevEndpoints.cs` `/dev/join`: `async (DevGuest guest, UpdateHandler handler, StateStore store) => … $"/start {store.Snapshot.Default().JoinCode}"` (add `using EventPhotoBot.State;`).

Build: `dotnet build`
Expected: `src/` builds. Test compilation fails until Step 10.

- [ ] **Step 10: Move the existing tests onto the new model**

Apply these replacements across the test files listed under **Files**, in this order:

| Find | Replace |
|---|---|
| `.Settings.Senders` | `.Senders` |
| `.Settings.Groups` | `.Groups` |
| `.Settings.EventName` | `.Default().Name` |
| any other `.Settings.<X>` on an `EventState` (e.g. `_factory.Store.Snapshot.Settings.KenBurns`, `s.Settings.SlideSeconds`) | `.Default().Settings.<X>` |
| `new EventState()` used as the state under test (ManifestBuilderTests, StateStoreTests' `theirs`) | `TestState.New()` (add `using EventPhotoBot.Tests.Fakes;`) |
| `ManifestBuilder.Build(state, <gen>, Now` | `Build(state, <gen>` with the local helper below |
| every `new ImageRecord { … }` seeded in a test | add `EventId = StateMigration.DefaultEventId,` |

Also add `using EventPhotoBot.State;` wherever `StateMigration`/`Default()` is now used.

File-specific changes:

- **`ManifestBuilderTests.cs`**: add `private static Manifest Build(EventState state, long generation, string? joinUrl = null) => ManifestBuilder.Build(state, state.Default(), generation, Now, joinUrl);`. `new EventState { Settings = { Layout = layout } }` → `var state = TestState.New(); state.Default().Settings.Layout = layout;`, and the same for `ShowJoinInvite = false`. `StateWith` starts from `TestState.New()`.
- **`StateStoreTests.cs`**: in `Load_on_an_empty_bucket_starts_from_defaults` also assert `Assert.Equal(StateMigration.DefaultEventId, store.Snapshot.Default().Id);`. Add:

```csharp
    [Fact]
    public async Task Initialize_persists_a_migration_of_a_pre_events_file()
    {
        var objects = new InMemoryObjectStore();
        objects.ForceWrite(StateStore.StatePath,
            """{"images":{},"settings":{"eventName":"Sommerfest","senders":[{"id":1,"name":"Ada","status":"known"}]}}"""u8.ToArray());
        var store = new StateStore(objects, seed: new StateSeed("party2026"));

        await store.InitializeAsync();

        var written = (await objects.ReadAsync(StateStore.StatePath))!.Bytes;
        using var document = JsonDocument.Parse(written);
        Assert.True(document.RootElement.TryGetProperty("events", out _));
        Assert.False(document.RootElement.TryGetProperty("settings", out _));
        Assert.Equal("party2026", store.Snapshot.Default().JoinCode);
    }

    [Fact]
    public async Task Initialize_does_not_write_an_already_migrated_file()
    {
        var (store, objects) = NewStore();
        await store.InitializeAsync();            // fresh bucket: migrates and writes once
        var writes = objects.WriteCount;

        var again = new StateStore(objects);
        await again.InitializeAsync();

        Assert.Equal(writes, objects.WriteCount);
    }
```

- **`StateModelTests.cs`**: delete `A_state_file_from_before_groups_loads_with_no_groups` and `A_fresh_state_has_an_empty_roster`, which are now covered by the migration tests. Rewrite the remaining three against the new shape:

```csharp
    [Fact]
    public void A_sender_roster_round_trips_through_json()
    {
        var state = TestState.New();
        state.Senders.Add(new Sender
        {
            Id = 42, Name = "Guest", Banned = false, CurrentEventId = "daglig",
            FirstSeen = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            Memberships = [new Membership { EventId = "daglig", AutoApprove = true }],
        });

        var json = JsonSerializer.SerializeToUtf8Bytes(state, StateJson.Options);
        var sender = Assert.Single(JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!.Senders);

        Assert.Equal(42, sender.Id);
        Assert.Equal("daglig", sender.CurrentEventId);
        Assert.True(Assert.Single(sender.Memberships).AutoApprove);
    }

    [Fact]
    public void Groups_round_trip_through_json()
    {
        var state = TestState.New();
        state.Groups.Add(new BotGroup { Id = -1001234, Title = "Festkomiteen", EventId = "daglig" });

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var group = Assert.Single(JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!.Groups);

        Assert.Equal(-1001234, group.Id);
        Assert.Equal("daglig", group.EventId);
    }

    [Fact]
    public void State_round_trips_through_json_with_camel_case_enums()
    {
        var state = TestState.New();
        state.Default().Settings.TakeoverImageId = "01ABC";
        state.Images["01ABC"] = new ImageRecord
        {
            Id = "01ABC", EventId = "daglig", Source = ImageSource.Telegram, Sha256 = "deadbeef",
            Status = ImageStatus.Approved, Pin = PinKind.Recurring,
            SortKey = "2026-09-20T18:00:00Z", OriginalExtension = "jpg",
        };

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var back = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        Assert.Contains("\"telegram\"", json);
        Assert.Contains("\"recurring\"", json);
        Assert.Equal(ImageStatus.Approved, back.Images["01ABC"].Status);
        Assert.Equal("daglig", back.Images["01ABC"].EventId);
        Assert.Equal("01ABC", back.Default().Settings.TakeoverImageId);
    }
```

- **`AppConfigTests.cs`**: replace `Load_throws_when_the_join_code_is_missing` with:

```csharp
    [Fact]
    public void Load_accepts_a_missing_join_code()
    {
        var partial = Complete().Where(p => p.Item1 is not "JOIN_CODE").ToArray();

        Assert.Null(AppConfig.Load(Config(partial)).JoinCode);
    }
```

- **`BotIdentityTests.cs`**: `new BotIdentity(Config())` → `new BotIdentity()`, `identity.JoinUrl` → `identity.JoinUrlFor("party2026")`, and delete the now-unused `Config()` helper.
- **`UpdateHandlerTests.cs`**:
  - `Harness`: delete `Config()`. Create the store as `new StateStore(harness.Objects, seed: new StateSeed(JoinCode))`. Without the seed the default event gets a random code and every `/start {Harness.JoinCode}` test fails. `CreateAsync(Action<Settings>? configure = null)` → `CreateAsync(Action<EventState>? configure = null)` with `await harness.Store.MutateAsync(s => configure(s));`. Construct `new UpdateHandler(harness.Store, harness.Objects, harness.Telegram, NullLogger<UpdateHandler>.Instance)`.
  - Replace `Roster` with:

```csharp
    private static Sender Roster(long id, bool autoApprove = false, bool banned = false) => new()
    {
        Id = id, Name = "Guest", Banned = banned, FirstSeen = DateTimeOffset.UtcNow,
        CurrentEventId = StateMigration.DefaultEventId,
        Memberships = [new Membership { EventId = StateMigration.DefaultEventId, AutoApprove = autoApprove }],
    };
```

  - `Roster(X, SenderStatus.Known)` → `Roster(X)`. `Roster(X, SenderStatus.AutoApprove)` → `Roster(X, autoApprove: true)`. `Roster(X, SenderStatus.Banned)` → `Roster(X, banned: true)`.
  - Replace the `Listening(long id = Group)` helper with `Routed(long id = Group)`, which returns `new() { Id = id, Title = "Festkomiteen", EventId = StateMigration.DefaultEventId, FirstSeen = DateTimeOffset.UtcNow }`, and rename every call.
  - Assertion rewrites:
    - `Assert.Equal(SenderStatus.Known, sender.Status);` (in `The_join_code_admits_a_new_sender_as_known` and `A_new_members_first_photo_is_queued_and_adds_them_as_known`) → `Assert.False(sender.Banned); Assert.False(sender.MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);`
    - `Redeeming_the_code_again_does_not_demote_an_auto_approve_sender` → `Assert.True(Assert.Single(harness.Store.Snapshot.Senders).MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);`
    - `A_banned_sender_cannot_readmit_themselves_with_the_code` → `Assert.True(Assert.Single(harness.Store.Snapshot.Senders).Banned);`
    - `Redemption_is_not_blocked_by_the_decline_cooldown` → `Assert.NotNull(Assert.Single(harness.Store.Snapshot.Senders).MembershipIn(StateMigration.DefaultEventId));`
    - `Assert.False(group.Listening)` → `Assert.Null(group.EventId)`. `Assert.True(group.Listening)` → `Assert.Equal(StateMigration.DefaultEventId, group.EventId)`. `groups.Count(g => !g.Listening)` → `groups.Count(g => g.EventId is null)`.
    - `A_photo_in_a_group_the_bot_does_not_listen_to_is_ignored_without_a_write` seeds `new BotGroup { Id = Group, Title = "Festkomiteen" }`, which is already unrouted. No change.
- **`AdminApiTests.cs`**:
  - `SeedGroupAsync(long id, bool listening)` sets `EventId = listening ? StateMigration.DefaultEventId : null`.
  - `.Single(g => g.Id == X).Listening` → `.Single(g => g.Id == X).EventId is not null`, inside `Assert.True`/`Assert.False` as before.
  - In `Reading_settings_returns_the_full_shape_including_the_roster`, replace the seeding with:

```csharp
        await _factory.Store.MutateAsync(s =>
        {
            s.Default().Settings.SlideSeconds = 42;
            s.Default().Settings.Order = SlideOrder.NewestFirst;
            s.Default().Name = "Sommerfest";
            s.Default().Settings.Layout = SlideLayout.Mosaic;
            s.Senders =
            [
                new Sender
                {
                    Id = 42, Name = "Guest",
                    Memberships = [new Membership { EventId = StateMigration.DefaultEventId, AutoApprove = true }],
                },
            ];
        });
```

- **`SenderApiTests.cs`**:
  - `Assert.Equal(SenderStatus.AutoApprove, sender.Status)` → `Assert.True(sender.MembershipIn(StateMigration.DefaultEventId)!.AutoApprove)`.
  - `Assert.Equal(SenderStatus.Known, sender.Status)` → `Assert.False(sender.Banned); Assert.False(sender.MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);`.
  - `s.Settings.TakeoverImageId = id` → `s.Default().Settings.TakeoverImageId = id`.
- **`DeleteAllImagesTests.cs`**, **`TakeoverInvariantTests.cs`**, **`ManifestEndpointTests.cs`**: only the table's replacements.

- [ ] **Step 11: Run the whole suite**

Run: `dotnet test`
Expected: PASS with 0 failures. The count is 266 − 2 deleted `StateModelTests` + 6 migration + 2 store tests = 272.

- [ ] **Step 12: Commit**

```bash
git add -A src tests
git commit -m "feat(state): events as data, with a pre-events file migrated on load

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Event lifecycle and the events API

**Files:**
- Create: `src/EventPhotoBot/State/ImageObjects.cs`, `src/EventPhotoBot/Web/EventEndpoints.cs`, `tests/EventPhotoBot.Tests/EventRulesTests.cs`, `tests/EventPhotoBot.Tests/EventApiTests.cs`
- Modify: `src/EventPhotoBot/State/EventRules.cs`, `src/EventPhotoBot/Web/ApiEndpoints.cs` (delete paths use `ImageObjects`), `src/EventPhotoBot/Program.cs`, `tests/EventPhotoBot.Tests/Fakes/TestState.cs`, `tests/EventPhotoBot.Tests/EndpointAuthTests.cs`

**Interfaces:**
- Consumes: Task 1's model and `EventRules`.
- Produces:
  - `enum EventPhase { Scheduled, Open, Closed }`
  - `EventRules.IsOpen(this Event, DateTimeOffset) → bool`, `EventRules.PhaseAt(this Event, DateTimeOffset) → EventPhase`, `EventRules.UniqueJoinCode(EventState) → string`
  - `ImageObjects.DeleteAsync(IObjectStore, ImageRecord, CancellationToken)`
  - `EventEndpoints.MapEvents(this WebApplication)`, `EventEndpoints.CleanName(string?) → string?`, `EventEndpoints.MaxNameLength = 100`
  - Routes: `GET/POST /api/events`, `PATCH/DELETE /api/events/{id}`, `PUT /api/events/{id}/schedule`, `POST /api/events/{id}/close`, `/reopen`, `/rotate-code`
  - Test helper `TestState.AddEvent(this EventState, string id, string? name = null, string? joinCode = null, bool closed = false) → Event`

- [ ] **Step 1: Write the rules tests**

`tests/EventPhotoBot.Tests/EventRulesTests.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class EventRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static Event At(DateTimeOffset? opens = null, DateTimeOffset? closes = null, DateTimeOffset? closedAt = null) =>
        new() { Id = "x", JoinCode = "c", OpensAt = opens, ClosesAt = closes, ClosedAt = closedAt };

    [Fact] public void No_times_is_open() => Assert.Equal(EventPhase.Open, At().PhaseAt(Now));
    [Fact] public void Before_opens_at_is_scheduled() => Assert.Equal(EventPhase.Scheduled, At(opens: Now.AddHours(1)).PhaseAt(Now));
    [Fact] public void At_opens_at_is_open() => Assert.True(At(opens: Now).IsOpen(Now));
    [Fact] public void At_closes_at_is_closed() => Assert.Equal(EventPhase.Closed, At(closes: Now).PhaseAt(Now));
    [Fact] public void A_manual_close_wins_over_the_schedule() =>
        Assert.Equal(EventPhase.Closed, At(opens: Now.AddHours(1), closedAt: Now.AddHours(-1)).PhaseAt(Now));

    [Fact]
    public void Generated_codes_are_unique_among_the_events()
    {
        var state = new EventState();
        StateMigration.Migrate(state, "party2026", Now);

        var code = EventRules.UniqueJoinCode(state);

        Assert.NotEqual("party2026", code);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", code);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~EventRulesTests"`
Expected: build FAILS: `EventPhase`, `PhaseAt`, `IsOpen` and `UniqueJoinCode` are not defined.

- [ ] **Step 3: Implement the rules**

Append to `EventRules` in `src/EventPhotoBot/State/EventRules.cs`:

```csharp
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
```

and, above the class, `public enum EventPhase { Scheduled, Open, Closed }`.

Run: `dotnet test --filter "FullyQualifiedName~EventRulesTests"`
Expected: PASS.

- [ ] **Step 4: Write the events API tests**

Append to `tests/EventPhotoBot.Tests/Fakes/TestState.cs`:

```csharp
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
```

Create `tests/EventPhotoBot.Tests/EventApiTests.cs`. Its own `AppFactory` via `IClassFixture`. Each test uses its own slug, because the store is shared across the class.

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class EventApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public EventApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateAuthenticatedClient();
    private Event? Find(string id) => _factory.Store.Snapshot.Find(id);

    [Fact]
    public async Task Creating_an_event_gives_it_a_fresh_code_and_default_settings()
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "bryllup-1", name = "  Bryllup  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = Find("bryllup-1")!;
        Assert.Equal("Bryllup", ev.Name);
        Assert.False(ev.IsDefault);
        Assert.NotEqual(_factory.Store.Snapshot.Default().JoinCode, ev.JoinCode);
        Assert.Null(ev.Retention.MaxAgeDays);
    }

    [Theory]
    [InlineData("Bryllup")]      // upper case
    [InlineData("bryllup 2")]    // space
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // 33 characters
    public async Task An_invalid_slug_is_rejected(string id)
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id, name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_slug_is_a_conflict()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "dup", name = "A" });
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "dup", name = "B" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("A", Find("dup")!.Name);
    }

    [Fact]
    public async Task An_empty_name_is_rejected()
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "noname", name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Find("noname"));
    }

    [Fact]
    public async Task Opening_after_closing_is_rejected()
    {
        var at = DateTimeOffset.UtcNow;
        var response = await Client.PostAsJsonAsync("/api/events",
            new { id = "backwards", name = "X", opensAt = at.AddHours(2), closesAt = at.AddHours(1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_list_puts_the_default_first_and_names_each_phase()
    {
        await Client.PostAsJsonAsync("/api/events",
            new { id = "later", name = "Senere", opensAt = DateTimeOffset.UtcNow.AddDays(1) });

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/events");

        var first = list.EnumerateArray().First();
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal("open", first.GetProperty("phase").GetString());
        var later = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "later");
        Assert.Equal("scheduled", later.GetProperty("phase").GetString());
        Assert.True(later.TryGetProperty("joinUrl", out _));
    }

    [Fact]
    public async Task Close_and_reopen()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "konsert", name = "Konsert" });

        await Client.PostAsync("/api/events/konsert/close", null);
        Assert.False(Find("konsert")!.IsOpen(DateTimeOffset.UtcNow));

        await Client.PostAsync("/api/events/konsert/reopen", null);
        Assert.True(Find("konsert")!.IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Reopening_an_event_whose_end_time_has_passed_clears_the_end_time()
    {
        // Review focus 5: without this the event stays closed by its clock after a reopen.
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.AddEvent("utgatt");
            ev.ClosesAt = DateTimeOffset.UtcNow.AddHours(-1);
        });

        await Client.PostAsync("/api/events/utgatt/reopen", null);

        Assert.Null(Find("utgatt")!.ClosesAt);
        Assert.True(Find("utgatt")!.IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_schedule_can_be_set_and_cleared()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "planlagt", name = "P" });
        var opens = DateTimeOffset.UtcNow.AddDays(1);

        await Client.PutAsJsonAsync("/api/events/planlagt/schedule", new { opensAt = opens, closesAt = (DateTimeOffset?)null });
        Assert.Equal(opens, Find("planlagt")!.OpensAt);

        await Client.PutAsJsonAsync("/api/events/planlagt/schedule", new { opensAt = (DateTimeOffset?)null, closesAt = (DateTimeOffset?)null });
        Assert.Null(Find("planlagt")!.OpensAt);
    }

    [Fact]
    public async Task Renaming_keeps_the_rules_for_names()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "navn", name = "Før" });

        Assert.Equal(HttpStatusCode.OK, (await Client.PatchAsJsonAsync("/api/events/navn", new { name = "Etter" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync("/api/events/navn", new { name = "" })).StatusCode);
        Assert.Equal("Etter", Find("navn")!.Name);
    }

    [Fact]
    public async Task Rotating_the_code_replaces_it()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "roter", name = "R" });
        var before = Find("roter")!.JoinCode;

        await Client.PostAsync("/api/events/roter/rotate-code", null);

        Assert.NotEqual(before, Find("roter")!.JoinCode);
    }

    [Theory]
    [InlineData("POST", "/api/events/daglig/close")]
    [InlineData("DELETE", "/api/events/daglig")]
    [InlineData("PUT", "/api/events/daglig/schedule")]
    public async Task The_default_event_cannot_be_closed_deleted_or_scheduled(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { opensAt = DateTimeOffset.UtcNow.AddDays(1) }) : null,
        };

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(_factory.Store.Snapshot.Default().IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Deleting_an_event_removes_its_photos_memberships_and_routes()
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s =>
        {
            s.AddEvent("slett");
            s.Images[id] = new ImageRecord
            {
                Id = id, EventId = "slett", Sha256 = id, SortKey = id, OriginalExtension = "jpg",
            };
            s.Senders.Add(new Sender
            {
                Id = 7001, CurrentEventId = "slett",
                Memberships = [new Membership { EventId = "slett" }, new Membership { EventId = "daglig" }],
            });
            s.Groups.Add(new BotGroup { Id = -7001, EventId = "slett" });
        });

        var response = await Client.DeleteAsync("/api/events/slett");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = _factory.Store.Snapshot;
        Assert.Null(state.Find("slett"));
        Assert.DoesNotContain(id, state.Images.Keys);
        Assert.DoesNotContain(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
        var sender = state.Senders.Single(s => s.Id == 7001);
        Assert.Null(sender.CurrentEventId);
        Assert.Equal(["daglig"], sender.Memberships.Select(m => m.EventId));
        Assert.Null(state.Groups.Single(g => g.Id == -7001).EventId);
    }

    [Fact]
    public async Task An_unknown_event_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/events/nope/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync("/api/events/nope")).StatusCode);
    }
}
```

In `EndpointAuthTests.cs` add `[InlineData("/api/events")]` to `Api_and_image_paths_return_401_without_a_session`.

- [ ] **Step 5: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~EventApiTests"`
Expected: FAIL. Every request returns 404 because the routes don't exist.

- [ ] **Step 6: Implement `ImageObjects` and the endpoints**

`src/EventPhotoBot/State/ImageObjects.cs`:

```csharp
namespace EventPhotoBot.State;

public static class ImageObjects
{
    /// <summary>
    /// An image's three objects. Called after the state write that removed its record:
    /// a record pointing at deleted bytes would put a broken image on the projector,
    /// while bytes nothing points at are invisible.
    /// </summary>
    public static async Task DeleteAsync(IObjectStore objects, ImageRecord image, CancellationToken ct = default)
    {
        await objects.DeleteAsync(ObjectPaths.Display(image.Id), ct);
        await objects.DeleteAsync(ObjectPaths.Thumb(image.Id), ct);
        await objects.DeleteAsync(ObjectPaths.Original(image.Id, image.OriginalExtension), ct);
    }
}
```

In `ApiEndpoints.cs`, replace the three-line object deletes in `DELETE /api/images/{id}` and `DELETE /api/images` with `await ImageObjects.DeleteAsync(objects, removed, ct);` and `foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);`.

`src/EventPhotoBot/Web/EventEndpoints.cs`:

```csharp
using System.Text.RegularExpressions;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;

namespace EventPhotoBot.Web;

public sealed record CreateEventRequest(string? Id, string? Name, DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);
public sealed record EventPatch(string? Name);

/// <summary>Replaces both times: a null clears one. The default event has no schedule.</summary>
public sealed record ScheduleRequest(DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);

public static class EventEndpoints
{
    public const int MaxNameLength = 100;
    private static readonly Regex SlugPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    /// <summary>
    /// Trimmed, and truncated rather than rejected past the limit, as the event name
    /// always was. Null when nothing is left: the bot names the event in every
    /// acknowledgement, so an event without a name would answer "Mottatt til ".
    /// </summary>
    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    internal static IResult BadRequest(string error) => Results.BadRequest(new { error });

    private static string? ScheduleError(DateTimeOffset? opensAt, DateTimeOffset? closesAt) =>
        opensAt is { } o && closesAt is { } c && o >= c
            ? "Starttidspunktet må være før sluttidspunktet."
            : null;

    public static void MapEvents(this WebApplication app)
    {
        app.MapGet("/api/events", (StateStore store, BotIdentity identity) =>
        {
            var state = store.Snapshot;
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(state.Events
                .OrderByDescending(e => e.IsDefault)
                .ThenByDescending(e => e.CreatedAt)
                .Select(e => View(state, e, now, identity)));
        });

        app.MapPost("/api/events", async (CreateEventRequest request, StateStore store) =>
        {
            var id = request.Id?.Trim() ?? "";
            if (!SlugPattern.IsMatch(id))
                return BadRequest("id må være 1–32 tegn: små bokstaver a–z, sifre og bindestrek.");
            if (CleanName(request.Name) is not { } name) return BadRequest("Navnet kan ikke være tomt.");
            if (ScheduleError(request.OpensAt, request.ClosesAt) is { } error) return BadRequest(error);

            var conflict = Results.Conflict(new { error = "Det finnes allerede et arrangement med den id-en." });
            if (store.Snapshot.Find(id) is not null) return conflict;

            return await store.MutateAsync(state =>
            {
                if (state.Find(id) is not null) return conflict;
                state.Events.Add(new Event
                {
                    Id = id,
                    Name = name,
                    JoinCode = EventRules.UniqueJoinCode(state),
                    OpensAt = request.OpensAt,
                    ClosesAt = request.ClosesAt,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                return Results.Created($"/api/events/{id}", new { id });
            });
        });

        app.MapPatch("/api/events/{id}", async (string id, EventPatch patch, StateStore store) =>
        {
            if (CleanName(patch.Name) is not { } name) return BadRequest("Navnet kan ikke være tomt.");
            return await Change(store, id, ev => { ev.Name = name; return null; });
        });

        app.MapPut("/api/events/{id}/schedule", async (string id, ScheduleRequest request, StateStore store) =>
        {
            if (ScheduleError(request.OpensAt, request.ClosesAt) is { } error) return BadRequest(error);
            return await Change(store, id, ev =>
            {
                if (ev.IsDefault) return BadRequest("Standardarrangementet har ingen tidsplan.");
                ev.OpensAt = request.OpensAt;
                ev.ClosesAt = request.ClosesAt;
                return null;
            });
        });

        app.MapPost("/api/events/{id}/close", async (string id, StateStore store) =>
            await Change(store, id, ev =>
            {
                if (ev.IsDefault) return BadRequest("Standardarrangementet kan ikke stenges.");
                ev.ClosedAt ??= DateTimeOffset.UtcNow;
                return null;
            }));

        app.MapPost("/api/events/{id}/reopen", async (string id, StateStore store) =>
            await Change(store, id, ev =>
            {
                var now = DateTimeOffset.UtcNow;
                ev.ClosedAt = null;
                // An end time already behind us would keep the event closed by its own
                // clock, and "Åpne igjen" would look like it did nothing.
                if (ev.ClosesAt <= now) ev.ClosesAt = null;
                return null;
            }));

        app.MapPost("/api/events/{id}/rotate-code", async (string id, StateStore store) =>
            await Change(store, id, ev => null, (state, ev) => ev.JoinCode = EventRules.UniqueJoinCode(state)));

        app.MapDelete("/api/events/{id}",
            async (string id, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                if (store.Snapshot.Find(id) is not { } target) return EventScope.UnknownEvent();
                if (target.IsDefault) return BadRequest("Standardarrangementet kan ikke slettes.");

                var removed = await store.MutateAsync(state =>
                {
                    if (state.Find(id) is not { IsDefault: false } ev) return null;
                    state.Events.Remove(ev);

                    var images = state.Images.Values.Where(i => i.EventId == id).ToList();
                    foreach (var image in images) state.Images.Remove(image.Id);
                    foreach (var sender in state.Senders)
                    {
                        sender.Memberships.RemoveAll(m => m.EventId == id);
                        if (sender.CurrentEventId == id) sender.CurrentEventId = null;
                    }
                    // Un-routed without a notice: the group is told when it is routed
                    // somewhere, not when it stops being collected from.
                    foreach (var group in state.Groups.Where(g => g.EventId == id)) group.EventId = null;
                    return images;
                }, ct);

                if (removed is null) return EventScope.UnknownEvent();
                foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);
                return Results.Ok(new { deleted = removed.Count });
            });
    }

    /// <summary>
    /// One event changed in one write. <paramref name="check"/> may refuse with a
    /// result; <paramref name="apply"/> runs when the whole state is needed as well.
    /// </summary>
    private static async Task<IResult> Change(StateStore store, string id,
        Func<Event, IResult?> check, Action<EventState, Event>? apply = null)
    {
        if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();
        return await store.MutateAsync(state =>
        {
            if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
            if (check(ev) is { } refused) return refused;
            apply?.Invoke(state, ev);
            return Results.Ok();
        });
    }

    private static object View(EventState state, Event e, DateTimeOffset now, BotIdentity identity)
    {
        var images = state.Images.Values.Where(i => i.EventId == e.Id).ToList();
        return new
        {
            e.Id,
            e.Name,
            e.IsDefault,
            Phase = e.PhaseAt(now).ToString().ToLowerInvariant(),
            e.OpensAt,
            e.ClosesAt,
            e.ClosedAt,
            e.CreatedAt,
            e.JoinCode,
            JoinUrl = identity.JoinUrlFor(e.JoinCode),
            Retention = new { e.Retention.MaxAgeDays, e.Retention.KeepNewest },
            Counts = new
            {
                Total = images.Count,
                Approved = images.Count(i => i.Status == ImageStatus.Approved),
                Pending = images.Count(i => i.Status == ImageStatus.Pending),
            },
        };
    }
}
```

Two notes on this code:
- `Change` writes even when `check` refuses. That matches the existing endpoints' "not found inside the lock" behaviour: `MutateAsync` always writes.
- Refusing before the lookup as well would need a second read of the snapshot, and nothing here needs it.

Create `src/EventPhotoBot/Web/EventScope.cs` now, because `UnknownEvent` is used above. Task 3 extends it.

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class EventScope
{
    public static IResult UnknownEvent() => Results.NotFound(new { error = "Ukjent arrangement." });
}
```

In `Program.cs`, after `app.MapApi();` add `app.MapEvents();`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat(events): create, schedule, close, rotate and delete events

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Scope the existing admin API and the manifest by `?event=`

**Files:**
- Modify: `src/EventPhotoBot/Web/EventScope.cs`, `src/EventPhotoBot/Web/ApiEndpoints.cs`, `src/EventPhotoBot/Web/ManifestBuilder.cs`, `src/EventPhotoBot/Web/QrEndpoint.cs`
- Create: `tests/EventPhotoBot.Tests/ScopedApiTests.cs`
- Modify tests: `ManifestBuilderTests.cs`, `AdminApiTests.cs` (the "event name can be cleared" test)

**Interfaces:**
- Consumes: `EventRules.IsOpen`/`PhaseAt` (Task 2), `EventEndpoints.CleanName` (Task 2).
- Produces:
  - `EventScope.Resolve(EventState, string? eventId) → Event?`: `null`/`""` means the default event, and an unknown id gives `null`.
  - The query parameter is named `event` on: `GET /api/images` (absent means every event), and on `GET/PATCH /api/settings`, `DELETE /api/takeover`, `DELETE /api/images`, `POST /api/images`, `GET /api/manifest` and `GET /api/join-qr.svg` (absent means the default event).
  - `GET /api/settings` gains `eventId`.
  - `GET /api/images` entries gain `eventId`.
  - `Manifest.PendingTotal` counts pending images across open events.
  - Manifest ETag: `"{generation}-{eventId}-{open|closed}"`.

- [ ] **Step 1: Write the scoping tests**

`tests/EventPhotoBot.Tests/ScopedApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

/// <summary>Its own factory: these tests add events and clear whole events' images.</summary>
public class ScopedApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ScopedApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task EnsureEventAsync(string id, bool closed = false) =>
        await _factory.Store.MutateAsync(s => { if (s.Find(id) is null) s.AddEvent(id, name: id.ToUpperInvariant(), closed: closed); });

    private async Task<string> SeedAsync(string eventId, ImageStatus status = ImageStatus.Approved)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = eventId, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg", ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private static IEnumerable<string?> Ids(JsonElement list) =>
        list.EnumerateArray().Select(e => e.GetProperty("id").GetString());

    [Fact]
    public async Task The_image_list_filters_by_event_and_lists_every_event_without_one()
    {
        await EnsureEventAsync("a1");
        var daily = await SeedAsync("daglig");
        var special = await SeedAsync("a1");

        var scoped = await Client.GetFromJsonAsync<JsonElement>("/api/images?event=a1");
        var all = await Client.GetFromJsonAsync<JsonElement>("/api/images");

        Assert.Contains(special, Ids(scoped));
        Assert.DoesNotContain(daily, Ids(scoped));
        Assert.Contains(daily, Ids(all));
        Assert.Contains(special, Ids(all));
        Assert.Equal("a1", scoped.EnumerateArray().First().GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task Settings_are_per_event()
    {
        await EnsureEventAsync("a2");

        await Client.PatchAsJsonAsync("/api/settings?event=a2", new { slideSeconds = 33, eventName = "Konsert" });

        Assert.Equal(33, _factory.Store.Snapshot.Find("a2")!.Settings.SlideSeconds);
        Assert.Equal("Konsert", _factory.Store.Snapshot.Find("a2")!.Name);
        Assert.NotEqual(33, _factory.Store.Snapshot.Default().Settings.SlideSeconds);
        var read = await Client.GetFromJsonAsync<JsonElement>("/api/settings?event=a2");
        Assert.Equal("a2", read.GetProperty("eventId").GetString());
        Assert.Equal(33, read.GetProperty("slideSeconds").GetInt32());
    }

    [Fact]
    public async Task Takeover_lands_on_the_images_own_event()
    {
        await EnsureEventAsync("a3");
        var image = await SeedAsync("a3");

        await Client.PutAsJsonAsync("/api/takeover", new { imageId = image, minutes = (int?)null });

        Assert.Equal(image, _factory.Store.Snapshot.Find("a3")!.Settings.TakeoverImageId);
        Assert.NotEqual(image, _factory.Store.Snapshot.Default().Settings.TakeoverImageId);

        await Client.DeleteAsync("/api/takeover?event=a3");
        Assert.Null(_factory.Store.Snapshot.Find("a3")!.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Delete_all_clears_only_the_named_event()
    {
        await EnsureEventAsync("a4");
        var keep = await SeedAsync("daglig");
        var gone = await SeedAsync("a4");

        await Client.DeleteAsync("/api/images?event=a4");

        Assert.Contains(keep, _factory.Store.Snapshot.Images.Keys);
        Assert.DoesNotContain(gone, _factory.Store.Snapshot.Images.Keys);
    }

    [Fact]
    public async Task An_upload_goes_to_the_named_event()
    {
        await EnsureEventAsync("a5");
        using var form = new MultipartFormDataContent();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));
        form.Add(new ByteArrayContent(bytes), "file", "photo.jpg");

        var response = await Client.PostAsync("/api/images?event=a5", form);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal("a5", _factory.Store.Snapshot.Images[id].EventId);
    }

    [Fact]
    public async Task The_manifest_is_per_event_and_its_etag_names_the_event()
    {
        await EnsureEventAsync("a6");
        var special = await SeedAsync("a6");

        var dailyResponse = await Client.GetAsync("/api/manifest");
        var specialResponse = await Client.GetAsync("/api/manifest?event=a6");
        var manifest = await specialResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(dailyResponse.Headers.ETag!.Tag, specialResponse.Headers.ETag!.Tag);
        Assert.Contains(special, Ids(manifest.GetProperty("images")));
        Assert.Equal("A6", manifest.GetProperty("settings").GetProperty("eventName").GetString());
    }

    [Fact]
    public async Task An_event_closing_on_its_clock_changes_the_etag_without_a_write()
    {
        // Review focus 2: nothing writes at ClosesAt, so the generation stays put.
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.Find("a7") ?? s.AddEvent("a7");
            ev.ClosesAt = DateTimeOffset.UtcNow.AddSeconds(1);
        });
        var open = await Client.GetAsync("/api/manifest?event=a7");
        var generation = _factory.Store.Generation;

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest?event=a7");
        request.Headers.TryAddWithoutValidation("If-None-Match", open.Headers.ETag!.ToString());
        var closed = await Client.SendAsync(request);

        Assert.Equal(generation, _factory.Store.Generation);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var settings = (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settings");
        Assert.False(settings.GetProperty("showJoinInvite").GetBoolean());
    }

    [Fact]
    public async Task The_qr_is_refused_for_a_closed_event_and_served_for_a_scheduled_one()
    {
        await EnsureEventAsync("a8", closed: true);
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.Find("a9") ?? s.AddEvent("a9");
            ev.OpensAt = DateTimeOffset.UtcNow.AddDays(1);
        });

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/join-qr.svg?event=a8")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/join-qr.svg?event=a9")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/images?event=nope")]
    [InlineData("GET", "/api/settings?event=nope")]
    [InlineData("GET", "/api/manifest?event=nope")]
    [InlineData("DELETE", "/api/images?event=nope")]
    [InlineData("DELETE", "/api/takeover?event=nope")]
    public async Task An_unknown_event_is_404(string method, string path)
    {
        var response = await Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_event_name_is_rejected_and_the_name_kept()
    {
        await EnsureEventAsync("a10");

        var response = await Client.PatchAsJsonAsync("/api/settings?event=a10", new { eventName = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A10", _factory.Store.Snapshot.Find("a10")!.Name);
    }
}
```

In `AdminApiTests.cs`, delete `The_event_name_can_be_cleared`, which contradicts the new rule. The new rule is covered above.

Append to `ManifestBuilderTests.cs`:

```csharp
    [Fact]
    public void Another_events_images_and_pending_count_stay_out()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None), ("p", ImageStatus.Pending, PinKind.None));
        state.AddEvent("other");
        state.Images["o"] = new ImageRecord
        {
            Id = "o", EventId = "other", Status = ImageStatus.Approved, Sha256 = "o", SortKey = "9999", OriginalExtension = "jpg",
        };

        var manifest = Build(state, 1);

        Assert.Equal(["a"], manifest.Images.Select(i => i.Id));
        Assert.Equal(1, manifest.PendingCount);
    }

    [Fact]
    public void The_pending_total_counts_every_open_event_and_skips_closed_ones()
    {
        var state = StateWith(("p1", ImageStatus.Pending, PinKind.None));
        state.AddEvent("open");
        state.AddEvent("shut", closed: true);
        foreach (var (id, ev) in new[] { ("p2", "open"), ("p3", "shut") })
            state.Images[id] = new ImageRecord
            {
                Id = id, EventId = ev, Status = ImageStatus.Pending, Sha256 = id, SortKey = id, OriginalExtension = "jpg",
            };

        Assert.Equal(2, Build(state, 1).PendingTotal);
    }

    [Fact]
    public void A_closed_event_hides_the_invite_whatever_the_setting_says()
    {
        var state = TestState.New();
        var ev = state.AddEvent("shut", closed: true);

        var manifest = ManifestBuilder.Build(state, ev, 1, Now, "https://t.me/bot?start=x");

        Assert.False(manifest.Settings.ShowJoinInvite);
        Assert.Null(manifest.Settings.JoinUrl);
    }
```

(`ManifestBuilder.Build` is called directly in the last test with a real `Now`, because `AddEvent(closed: true)` sets `ClosedAt` from the wall clock.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ScopedApiTests|FullyQualifiedName~ManifestBuilderTests"`
Expected: build FAILS on `PendingTotal`. After adding a stub, the scoping tests fail on the assertions.

- [ ] **Step 3: Implement**

`EventScope.cs`: add

```csharp
    /// <summary>
    /// The event a request names with ?event=, or the default event when it names none,
    /// so every page and screen written before events keeps meaning the church's own.
    /// Null for an id no event has.
    /// </summary>
    public static Event? Resolve(EventState state, string? eventId) =>
        string.IsNullOrEmpty(eventId) ? state.Default() : state.Find(eventId);
```

`ManifestBuilder.cs`:

- `Manifest` gains a last member `int PendingTotal`.
- In `Build`: `var open = ev.IsOpen(now);`. The `SettingsView` gets `open ? joinUrl : null` for `JoinUrl` and `settings.ShowJoinInvite && open` for `ShowJoinInvite`, with the comment: `// A closed event's screen keeps its photos but stops inviting anyone: the bot would only answer that the event is over.`
- Compute

```csharp
        var openEvents = state.Events.Where(e => e.IsOpen(now)).Select(e => e.Id).ToHashSet();
        var pendingTotal = state.Images.Values.Count(i => i.Status == ImageStatus.Pending && openEvents.Contains(i.EventId));
```

  and pass `PendingTotal: pendingTotal`.

`ApiEndpoints.cs` (add `using Microsoft.AspNetCore.Mvc;`; the parameter is always `[FromQuery(Name = "event")] string? eventId`, because `event` is a C# keyword):

- `GET /api/images`: after `var images = …`, add

```csharp
            if (!string.IsNullOrEmpty(eventId))
            {
                if (store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();
                images = images.Where(i => i.EventId == eventId);
            }
```

  and add `i.EventId` to the projection.
- `GET /api/settings`: `if (EventScope.Resolve(state, eventId) is not { } ev) return EventScope.UnknownEvent();` replaces `var ev = state.Default();`, and add `EventId = ev.Id,` as the first property.
- `PATCH /api/settings`: resolve first (404), then validate the name before the mutation:

```csharp
            string? name = null;
            if (patch.EventName is { } eventName && (name = EventEndpoints.CleanName(eventName)) is null)
                return Results.BadRequest(new { error = "Navnet kan ikke være tomt." });
            var targetId = target.Id;
```

  Inside the mutation, `if (state.Find(targetId) is not { } ev) return;` and `if (name is not null) ev.Name = name;`. Delete `MaxEventNameLength`, which now lives in `EventEndpoints`.
- `DELETE /api/takeover`: resolve (404), then clear that event's settings.
- `DELETE /api/images`: resolve (404). `var eventId = ev.Id;` replaces `store.Snapshot.Default().Id`, and the mutation clears `state.Find(eventId)!.Settings`'s takeover.
- `POST /api/images`: resolve before reading the form (404). Record `EventId = eventId` from the resolved event's `Id`.
- `GET /api/manifest`:

```csharp
        app.MapGet("/api/manifest",
            (HttpContext http, [FromQuery(Name = "event")] string? eventId, StateStore store, BotIdentity identity) =>
        {
            // Served entirely from memory. No object-store I/O on this path, ever:
            // it runs every two seconds per open page for the length of the event.
            var state = store.Snapshot;
            if (EventScope.Resolve(state, eventId) is not { } ev) return EventScope.UnknownEvent();
            var now = DateTimeOffset.UtcNow;

            // The generation alone is not enough: which event this is, and whether it is
            // open, change what the screen gets — and an event opens and closes on its
            // own clock, with no write to move the generation.
            var etag = $"\"{store.Generation}-{ev.Id}-{(ev.IsOpen(now) ? "open" : "closed")}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(ManifestBuilder.Build(state, ev, store.Generation, now, identity.JoinUrlFor(ev.JoinCode)));
        });
```

`QrEndpoint.cs`:

```csharp
        app.MapGet("/api/join-qr.svg",
            ([FromQuery(Name = "event")] string? eventId, StateStore store, BotIdentity identity) =>
        {
            // Served for a scheduled event too, so its QR can be printed in advance;
            // refused once it is over, when the code only earns a "that has ended".
            if (EventScope.Resolve(store.Snapshot, eventId) is not { } ev
                || ev.PhaseAt(DateTimeOffset.UtcNow) == EventPhase.Closed
                || identity.JoinUrlFor(ev.JoinCode) is not { } url)
                return Results.NotFound();
```

(add `using Microsoft.AspNetCore.Mvc;`).

- [ ] **Step 4: Run the suite**

Run: `dotnet test`
Expected: PASS. `A_poll_performs_no_object_store_io` still passes: the ETag is still computed from memory.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(api): scope settings, images, takeover and the manifest by event

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Route private-chat photos by the sender's current event

**Files:**
- Create: `src/EventPhotoBot/Telegram/Routing.cs`, `tests/EventPhotoBot.Tests/RoutingTests.cs`
- Modify: `src/EventPhotoBot/Telegram/UpdateHandler.cs`, `tests/EventPhotoBot.Tests/UpdateHandlerTests.cs`, `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs`

**Interfaces:**
- Consumes: `EventRules.IsOpen`/`PhaseAt`, `TestState.AddEvent`.
- Produces:
  - `Routing.ResolvePrivateTarget(EventState, Sender, DateTimeOffset) → Event?`
  - `UpdateHandler.IngestAsync(…, string expectedEventId, …)`, where the event is re-resolved under the lock
  - `FakeTelegramClient.OnDownload` (`Action?`), which runs inside `DownloadAsync`

- [ ] **Step 1: Write the routing tests**

`tests/EventPhotoBot.Tests/RoutingTests.cs`:

```csharp
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class RoutingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (EventState State, Sender Sender) Setup(string? current, bool closed, bool dailyMember, bool weddingMember = true)
    {
        var state = TestState.New();
        state.AddEvent("bryllup", closed: closed);
        var sender = new Sender { Id = 1, CurrentEventId = current };
        if (weddingMember) sender.Memberships.Add(new Membership { EventId = "bryllup" });
        if (dailyMember) sender.Memberships.Add(new Membership { EventId = "daglig" });
        return (state, sender);
    }

    [Fact]
    public void The_current_event_while_it_is_open()
    {
        var (state, sender) = Setup("bryllup", closed: false, dailyMember: true);
        Assert.Equal("bryllup", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void The_default_event_once_the_current_one_closes_for_a_member_of_it()
    {
        var (state, sender) = Setup("bryllup", closed: true, dailyMember: true);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void Nowhere_once_the_current_one_closes_for_someone_who_never_joined_the_default()
    {
        var (state, sender) = Setup("bryllup", closed: true, dailyMember: false);
        Assert.Null(Routing.ResolvePrivateTarget(state, sender, Now));
    }

    [Fact]
    public void The_default_event_when_the_current_one_was_deleted()
    {
        // Review focus 3.
        var (state, sender) = Setup("gone", closed: false, dailyMember: true);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }

    [Fact]
    public void Not_an_event_the_sender_is_no_longer_a_member_of()
    {
        var (state, sender) = Setup("bryllup", closed: false, dailyMember: true, weddingMember: false);
        Assert.Equal("daglig", Routing.ResolvePrivateTarget(state, sender, Now)?.Id);
    }
}
```

In `FakeTelegramClient.cs`, add `public Action? OnDownload { get; set; }` and call `OnDownload?.Invoke();` at the start of `DownloadAsync`.

Append to `UpdateHandlerTests.cs`, in a `// ---- Events ----` region:

```csharp
    private const string WeddingCode = "wedding-code";

    private static void Wedding(EventState s, bool closed = false) =>
        s.AddEvent("bryllup", name: "Bryllup", joinCode: WeddingCode, closed: closed);

    [Fact]
    public async Task A_new_sender_joining_a_special_event_is_a_member_of_that_event_only()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal(["bryllup"], sender.Memberships.Select(m => m.EventId));
        Assert.Equal("bryllup", sender.CurrentEventId);
        Assert.Contains("Bryllup", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_daily_member_switching_to_a_special_event_is_told_where_photos_go()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });

        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal("bryllup", sender.CurrentEventId);
        Assert.Equal(2, sender.Memberships.Count);
        Assert.Equal("Du sender nå bilder til Bryllup.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_photo_goes_to_the_current_event_and_the_reply_names_it()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("bryllup", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        Assert.StartsWith("Mottatt til Bryllup", harness.Telegram.Sent[^1].Text);
    }

    [Fact]
    public async Task Once_the_special_event_closes_a_daily_member_falls_back_silently()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            Wedding(s, closed: true);
            var guest = Roster(Guest);
            guest.CurrentEventId = "bryllup";
            guest.Memberships.Add(new Membership { EventId = "bryllup" });
            s.Senders.Add(guest);
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("daglig", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        // Only the acknowledgement; no "the wedding has ended" notice.
        Assert.StartsWith("Mottatt til Daglig", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task Once_the_special_event_closes_a_guest_of_only_that_event_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            Wedding(s, closed: true);
            s.Senders.Add(new Sender
            {
                Id = Guest, Name = "Guest", CurrentEventId = "bryllup",
                Memberships = [new Membership { EventId = "bryllup" }],
            });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
        Assert.Equal("Bryllup er avsluttet.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_code_for_a_closed_event_admits_nobody()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s, closed: true));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Equal("Bryllup er avsluttet.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_code_for_a_scheduled_event_admits_nobody_yet()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s).OpensAt = DateTimeOffset.UtcNow.AddDays(1));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Equal("Bryllup har ikke startet ennå.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task Auto_approve_in_the_daily_event_does_not_carry_over_to_a_special_one()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest, autoApprove: true)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Pending, Assert.Single(harness.Store.Snapshot.Images.Values).Status);
    }

    [Fact]
    public async Task The_same_photo_can_be_sent_to_two_events()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Equal(["bryllup", "daglig"], harness.Store.Snapshot.Images.Values.Select(i => i.EventId).Order());
    }

    [Fact]
    public async Task An_event_closing_during_the_download_is_seen_under_the_lock()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");
        harness.Telegram.OnDownload = () => harness.Store
            .MutateAsync(s => s.Find("bryllup")!.ClosedAt = DateTimeOffset.UtcNow.AddSeconds(-1))
            .GetAwaiter().GetResult();

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("daglig", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        Assert.StartsWith("Mottatt til Daglig", harness.Telegram.Sent[^1].Text);
    }
```

In the same file, update `Start_tells_a_known_sender_what_happens_to_their_photos`:

```csharp
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.StartsWith("Du sender bilder til Daglig.", text);
        Assert.DoesNotContain("slettes", text);
```

`A_wrong_join_code_admits_nobody` keeps its assertions: an unknown sender with a wrong code still gets the QR prompt.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~RoutingTests|FullyQualifiedName~UpdateHandlerTests"`
Expected: build FAILS on `Routing`. After Step 3's first file exists, the new handler tests fail on their assertions.

- [ ] **Step 3: Implement `Routing`**

`src/EventPhotoBot/Telegram/Routing.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public static class Routing
{
    /// <summary>
    /// Where a private-chat photo goes: the sender's current event while it is open;
    /// otherwise the default event, but only for someone who joined it — a wedding
    /// guest who never scanned the church's code must not end up on the church screen;
    /// otherwise nowhere.
    /// </summary>
    public static Event? ResolvePrivateTarget(EventState state, Sender sender, DateTimeOffset now)
    {
        if (state.Find(sender.CurrentEventId) is { } current
            && sender.MembershipIn(current.Id) is not null
            && current.IsOpen(now))
            return current;

        var fallback = state.Default();
        return sender.MembershipIn(fallback.Id) is not null && fallback.IsOpen(now) ? fallback : null;
    }
}
```

- [ ] **Step 4: Rewrite the private flow in `UpdateHandler`**

1. Replace everything in `HandleAsync` after the banned check (from the `if (entry?.MembershipIn(…) is null)` block to the end of the method) with:

```csharp
        if (message.Text is { } text && TryReadJoinCode(text, out var supplied))
        {
            await HandleJoinCodeAsync(sender, chat, entry, supplied, ct);
            return;
        }

        if (entry is null || entry.Memberships.Count == 0)
        {
            if (ShouldReplyToUnlisted(sender.Id))
                await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
            return;
        }

        if (message.Text is { } command && command.StartsWith("/start", StringComparison.Ordinal))
        {
            await SendCurrentEventAsync(chat, entry, ct);
            return;
        }

        var candidate = Extract(message);
        if (candidate is null)
        {
            await telegram.SendMessageAsync(chat.Id,
                "Bare bilder, takk — jeg kan ikke vise video, klistremerker eller animasjoner.", ct);
            return;
        }

        if (candidate.DeclineReason is { } reason)
        {
            await telegram.SendMessageAsync(chat.Id, reason, ct);
            return;
        }

        if (Routing.ResolvePrivateTarget(store.Snapshot, entry, DateTimeOffset.UtcNow) is not { } target)
        {
            // The one event they belong to is over. Throttled like the join prompt: a
            // guest sending twenty photos the day after the wedding gets one answer.
            if (ShouldReplyToUnlisted(sender.Id))
                await telegram.SendMessageAsync(chat.Id, $"{ClosedName(entry)} er avsluttet.", ct);
            return;
        }

        await IngestAsync(message, sender, chat, entry, candidate, target.Id, ct);
    }

    private string ClosedName(Sender entry) =>
        store.Snapshot.Find(entry.CurrentEventId)?.Name ?? "Arrangementet";
```

2. Replace `HandleUnredeemedAsync` with `HandleJoinCodeAsync` and `SendCurrentEventAsync`:

```csharp
    /// <summary>
    /// "/start CODE", from anyone who is not banned. A code for an open event joins it
    /// and makes it where this person's photos go; any other code gets said so. Never
    /// throttled when it matches: being rate-limited out of joining is the worst
    /// possible moment to go quiet on somebody.
    /// </summary>
    private async Task HandleJoinCodeAsync(
        TgUser sender, TgChat chat, Sender? entry, string supplied, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Event? match = null;
        // Every event's code is compared, not the first match: which event is being
        // joined must not show in how long the answer takes.
        foreach (var ev in store.Snapshot.Events)
            if (SecretComparison.Matches(ev.JoinCode, supplied)) match = ev;

        if (match is null)
        {
            if (entry is { Memberships.Count: > 0 }) await SendCurrentEventAsync(chat, entry, ct);
            else if (ShouldReplyToUnlisted(sender.Id)) await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
            return;
        }

        switch (match.PhaseAt(now))
        {
            case EventPhase.Closed:
                await telegram.SendMessageAsync(chat.Id, $"{match.Name} er avsluttet.", ct);
                return;
            case EventPhase.Scheduled:
                await telegram.SendMessageAsync(chat.Id, $"{match.Name} har ikke startet ennå.", ct);
                return;
        }

        var eventId = match.Id;
        var outcome = await store.MutateAsync(state =>
        {
            // Re-checked under the store's lock: two /start messages racing must not
            // produce two rows, and a ban landing meanwhile must win.
            var row = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
            var isNew = row is null;
            if (row is null)
            {
                row = new Sender { Id = sender.Id, Name = sender.DisplayName, FirstSeen = now };
                state.Senders.Add(row);
            }
            if (row.Banned) return (Joined: false, IsNew: false);
            if (row.MembershipIn(eventId) is null) row.Memberships.Add(new Membership { EventId = eventId });
            row.CurrentEventId = eventId;
            return (Joined: true, IsNew: isNew);
        }, ct);
        if (!outcome.Joined) return;

        await telegram.SendMessageAsync(chat.Id, outcome.IsNew
            ? $"Du er inne. Du sender nå bilder til {match.Name}. En arrangør godkjenner bildene før de vises på skjermen."
            : $"Du sender nå bilder til {match.Name}.", ct);
    }

    /// <summary>A bare /start from someone already in: where their photos go now.</summary>
    private async Task SendCurrentEventAsync(TgChat chat, Sender entry, CancellationToken ct)
    {
        var target = Routing.ResolvePrivateTarget(store.Snapshot, entry, DateTimeOffset.UtcNow);
        await telegram.SendMessageAsync(chat.Id, target is null
            ? $"{ClosedName(entry)} er avsluttet."
            : $"Du sender bilder til {target.Name}. Send meg bilder, så kommer de opp på skjermen når en arrangør har godkjent dem.",
            ct);
    }
```

   Delete the old `HandleUnredeemedAsync`, its known-sender `/start` branch in `HandleAsync`, and the "Du er inne … Alt slettes etter arrangementet." text.

3. `IngestAsync`: add a `string expectedEventId` parameter after `candidate`, and pass `ev.Id`-equivalent from the group path (`group.EventId!`, from the snapshot read in `HandleGroupMessageAsync`). Then:

   - Both fast-path duplicate checks become `store.Snapshot.Images.Values.Any(i => i.EventId == expectedEventId && …)`.
   - `enum IngestOutcome { Stored, Duplicate, Refused, Closed }`.
   - Inside `MutateAsync`, the private branch becomes:

```csharp
            else
            {
                // Re-read under the lock too: the event can close, and the sender be
                // banned, while the download and decode above were running.
                var row = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (row is null || row.Banned) return (IngestOutcome.Refused, "");
                if (Routing.ResolvePrivateTarget(state, row, now) is not { } target) return (IngestOutcome.Closed, "");
                eventId = target.Id;
                approved = row.MembershipIn(eventId)!.AutoApprove;
            }
```

   - The authoritative duplicate check moves below the event resolution and compares only `i.EventId == eventId`.
   - The mutation returns `(IngestOutcome Outcome, string EventId)`, and every `return IngestOutcome.X;` becomes `return (IngestOutcome.X, "")`. `Stored` returns `(IngestOutcome.Stored, eventId)`.
   - After the mutation:

```csharp
        if (outcome == IngestOutcome.Refused) return;
        if (outcome == IngestOutcome.Closed)
        {
            await ReplyAsync(chat, "Arrangementet er avsluttet, så bildet ble ikke lagret.", ct);
            return;
        }
        // (duplicate and group-reaction branches unchanged)
        var name = store.Snapshot.Find(storedEventId)?.Name ?? "";
        await AcknowledgeAsync(message, chat,
            approved ? $"Mottatt til {name} — det er på skjermen nå." : $"Mottatt til {name} — en arrangør godkjenner det snart.",
            ct);
```

   Here `outcome`/`storedEventId` come from deconstructing the tuple: `var (outcome, storedEventId) = await store.MutateAsync(...)`.

4. `HandleGroupMessageAsync` passes `group.EventId` (non-null at that point) as `expectedEventId`.

- [ ] **Step 5: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "feat(telegram): route private photos by the sender's current event

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Inline buttons for switching events

**Files:**
- Modify: `src/EventPhotoBot/Telegram/TelegramModels.cs`, `ITelegramClient.cs`, `TelegramClient.cs`, `OfflineTelegramClient.cs`, `Routing.cs`, `UpdateHandler.cs`, `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs`, `tests/EventPhotoBot.Tests/TelegramClientLoggingTests.cs`
- Create: `tests/EventPhotoBot.Tests/SwitchButtonTests.cs`, `tests/EventPhotoBot.Tests/TelegramClientPayloadTests.cs`

**Interfaces:**
- Produces:
  - `record InlineButton(string Text, string CallbackData)`
  - `TgUpdate.CallbackQuery` (`TgCallbackQuery { Id, From, Message, Data }`)
  - `ITelegramClient.SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)` (an overload), `AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default)`, `EditMessageTextAsync(long chatId, long messageId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)`
  - `Routing.SwitchButtons(EventState, Sender, string? currentEventId, DateTimeOffset) → IReadOnlyList<InlineButton>`
  - `Routing.CallbackPrefix = "ev:"`
  - `FakeTelegramClient.SentButtons`, `.Answers`, `.Edits`
  - `BotReply` gains `long MessageId` and `IReadOnlyList<InlineButton>? Buttons` (used by Task 13)

- [ ] **Step 1: Write the payload tests**

`tests/EventPhotoBot.Tests/TelegramClientPayloadTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using EventPhotoBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class TelegramClientPayloadTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Method, JsonElement Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Calls.Add((request.RequestUri!.AbsolutePath.Split('/')[^1], body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":true}""") };
        }
    }

    private static (TelegramClient Client, Capture Capture) Build()
    {
        var capture = new Capture();
        var config = new AppConfig
        {
            BucketName = "b", BotToken = "t", WebhookSecret = "s", WebhookPath = "p",
            AdminPassword = "a", CookieSigningKey = "0123456789abcdef0123456789abcdef",
        };
        return (new TelegramClient(new HttpClient(capture), config, NullLogger<TelegramClient>.Instance), capture);
    }

    [Fact]
    public async Task Buttons_go_one_per_row_as_an_inline_keyboard()
    {
        var (client, capture) = Build();

        await client.SendMessageAsync(5, "Hei", [new InlineButton("Daglig", "ev:daglig"), new InlineButton("Bryllup", "ev:bryllup")]);

        var rows = capture.Calls.Single().Body.GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("ev:bryllup", rows[1][0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task A_callback_answer_and_an_edit_carry_their_ids()
    {
        var (client, capture) = Build();

        await client.AnswerCallbackQueryAsync("cb1", "Bryllup er avsluttet.");
        await client.EditMessageTextAsync(5, 9, "Ny", []);

        Assert.Equal("answerCallbackQuery", capture.Calls[0].Method);
        Assert.Equal("cb1", capture.Calls[0].Body.GetProperty("callback_query_id").GetString());
        Assert.Equal("editMessageText", capture.Calls[1].Method);
        Assert.Equal(9, capture.Calls[1].Body.GetProperty("message_id").GetInt64());
    }
}
```

In `TelegramClientLoggingTests.Calls()`, add three more `yield return` entries:
- `c => c.SendMessageAsync(1, "hello", [new InlineButton("A", "ev:a")])`
- `c => c.AnswerCallbackQueryAsync("cb", null)`
- `c => c.EditMessageTextAsync(1, 2, "x", [])`

- [ ] **Step 2: Write the handler tests**

`tests/EventPhotoBot.Tests/SwitchButtonTests.cs`:

```csharp
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class SwitchButtonTests
{
    private const long Guest = 111;

    private static async Task<(UpdateHandler Handler, StateStore Store, FakeTelegramClient Telegram)> SetupAsync(
        Action<EventState> configure)
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(configure);
        var telegram = new FakeTelegramClient();
        return (new UpdateHandler(store, objects, telegram, NullLogger<UpdateHandler>.Instance), store, telegram);
    }

    private static Sender Member(params string[] events) => new()
    {
        Id = Guest, Name = "Guest", CurrentEventId = events[0],
        Memberships = [.. events.Select(e => new Membership { EventId = e })],
    };

    private static TgUpdate Tap(string data, long from = Guest) => new()
    {
        CallbackQuery = new TgCallbackQuery
        {
            Id = "cb1",
            From = new TgUser { Id = from, FirstName = "Guest" },
            Message = new TgMessage { MessageId = 50, Chat = new TgChat { Id = from, Type = "private" } },
            Data = data,
        },
    };

    [Fact]
    public async Task Joining_a_second_event_offers_a_button_back_to_the_first()
    {
        var (handler, _, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup", "wc"); s.Senders.Add(Member("daglig")); });

        await handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage { From = new TgUser { Id = Guest }, Chat = new TgChat { Id = Guest }, Text = "/start wc" },
        });

        var (_, _, buttons) = Assert.Single(telegram.SentButtons);
        Assert.Equal([new InlineButton("Daglig", "ev:daglig")], buttons);
    }

    [Fact]
    public async Task Tapping_a_button_switches_and_edits_the_message()
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup"); s.Senders.Add(Member("bryllup", "daglig")); });

        await handler.HandleAsync(Tap("ev:daglig"));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        var edit = Assert.Single(telegram.Edits);
        Assert.Equal((Guest, 50L, "Du sender nå bilder til Daglig."), (edit.ChatId, edit.MessageId, edit.Text));
        Assert.Equal([new InlineButton("Bryllup", "ev:bryllup")], edit.Buttons);
        Assert.Equal(("cb1", (string?)null), Assert.Single(telegram.Answers));
    }

    [Fact]
    public async Task Tapping_a_closed_event_changes_nothing_and_says_why()
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup", closed: true); s.Senders.Add(Member("daglig", "bryllup")); });

        await handler.HandleAsync(Tap("ev:bryllup"));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        Assert.Empty(telegram.Edits);
        Assert.Equal(("cb1", "Bryllup er avsluttet."), Assert.Single(telegram.Answers));
    }

    [Theory]
    [InlineData("ev:bryllup")]   // not a member
    [InlineData("ev:gone")]      // no such event
    [InlineData("junk")]         // not ours
    public async Task A_tap_that_does_not_apply_is_answered_and_ignored(string data)
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup"); s.Senders.Add(Member("daglig")); });

        await handler.HandleAsync(Tap(data));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        Assert.Empty(telegram.Edits);
        Assert.Single(telegram.Answers);
    }

    [Fact]
    public async Task A_banned_sender_gets_the_spinner_stopped_and_nothing_else()
    {
        var (handler, store, telegram) = await SetupAsync(s =>
        {
            var banned = Member("daglig");
            banned.Banned = true;
            s.Senders.Add(banned);
        });

        await handler.HandleAsync(Tap("ev:daglig"));

        Assert.Equal(("cb1", (string?)null), Assert.Single(telegram.Answers));
        Assert.Empty(telegram.Edits);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~SwitchButtonTests|FullyQualifiedName~TelegramClientPayloadTests"`
Expected: build FAILS: `InlineButton`, `TgCallbackQuery`, `AnswerCallbackQueryAsync` and the rest are not defined.

- [ ] **Step 4: Implement the models and the clients**

`TelegramModels.cs`:

```csharp
    // in TgUpdate:
    /// <summary>Someone tapped one of the bot's inline buttons.</summary>
    [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }

public sealed class TgCallbackQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public TgUser? From { get; set; }

    /// <summary>The bot's own message the button was on. Absent if it is too old for Telegram to say.</summary>
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }

    [JsonPropertyName("data")] public string? Data { get; set; }
}

/// <summary>One inline button under a message. Telegram caps CallbackData at 64 bytes.</summary>
public sealed record InlineButton(string Text, string CallbackData);
```

`InlineButton` is a record, so `Assert.Equal` on lists of them compares by value. The tests rely on this.

`ITelegramClient.cs`:

```csharp
    /// <summary>A message with one inline button per row under it.</summary>
    Task SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct = default);

    /// <summary>Stops the tapped button's spinner, optionally with a short toast.</summary>
    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default);

    Task EditMessageTextAsync(long chatId, long messageId, string text,
        IReadOnlyList<InlineButton> buttons, CancellationToken ct = default);
```

`TelegramClient.cs`, following `SendMessageAsync`'s pattern of "log a warning on failure, never throw on a non-success status":

```csharp
    public async Task SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/sendMessage",
            new { chat_id = chatId, text, reply_markup = Keyboard(buttons) }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("sendMessage failed: {StatusCode}", (int)response.StatusCode);
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/answerCallbackQuery",
            new { callback_query_id = callbackQueryId, text }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("answerCallbackQuery failed: {StatusCode}", (int)response.StatusCode);
    }

    public async Task EditMessageTextAsync(long chatId, long messageId, string text,
        IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/editMessageText",
            new { chat_id = chatId, message_id = messageId, text, reply_markup = Keyboard(buttons) }, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("editMessageText failed: {StatusCode}", (int)response.StatusCode);
    }

    private static object Keyboard(IReadOnlyList<InlineButton> buttons) => new
    {
        inline_keyboard = buttons.Select(b => new[] { new { text = b.Text, callback_data = b.CallbackData } }),
    };
```

`OfflineTelegramClient.cs`:
- `BotReply` becomes `public sealed record BotReply(long ChatId, string Text, DateTimeOffset At, bool Reaction = false, long MessageId = 0, IReadOnlyList<InlineButton>? Buttons = null, bool Edited = false);`
- Add `private long _nextMessageId;`. The existing `SendMessageAsync` records `MessageId: Interlocked.Increment(ref _nextMessageId)`.
- The buttons overload does the same with `Buttons: buttons`.
- `AnswerCallbackQueryAsync` logs and, if `text` is not null, records `new BotReply(0, text, DateTimeOffset.UtcNow)`.
- `EditMessageTextAsync` records `new BotReply(chatId, text, DateTimeOffset.UtcNow, MessageId: messageId, Buttons: buttons, Edited: true)`.

`FakeTelegramClient.cs`:

```csharp
    public List<(long ChatId, string Text, IReadOnlyList<InlineButton> Buttons)> SentButtons { get; } = [];
    public List<(string Id, string? Text)> Answers { get; } = [];
    public List<(long ChatId, long MessageId, string Text, IReadOnlyList<InlineButton> Buttons)> Edits { get; } = [];

    public Task SendMessageAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)
    {
        Sent.Add((chatId, text));   // so tests that only read Sent still see the text
        SentButtons.Add((chatId, text, buttons));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        Answers.Add((callbackQueryId, text));
        return Task.CompletedTask;
    }

    public Task EditMessageTextAsync(long chatId, long messageId, string text,
        IReadOnlyList<InlineButton> buttons, CancellationToken ct = default)
    {
        Edits.Add((chatId, messageId, text, buttons));
        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Implement the buttons in the handler**

`Routing.cs`:

```csharp
    public const string CallbackPrefix = "ev:";

    /// <summary>One button per other open event the sender belongs to; none when there is nothing to switch to.</summary>
    public static IReadOnlyList<InlineButton> SwitchButtons(EventState state, Sender sender, string? currentEventId, DateTimeOffset now) =>
        [.. sender.Memberships
            .Select(m => state.Find(m.EventId))
            .OfType<Event>()
            .Where(e => e.Id != currentEventId && e.IsOpen(now))
            .OrderByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .Select(e => new InlineButton(e.Name, CallbackPrefix + e.Id))];
```

`UpdateHandler.cs`:

1. At the top of `HandleAsync`:

```csharp
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, ct);
            return;
        }
```

2. Add a helper that sends with buttons only when there are any:

```csharp
    private Task SendAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct) =>
        buttons.Count == 0
            ? telegram.SendMessageAsync(chatId, text, ct)
            : telegram.SendMessageAsync(chatId, text, buttons, ct);
```

3. In `HandleJoinCodeAsync`, compute `var buttons = Routing.SwitchButtons(store.Snapshot, store.Snapshot.Senders.First(s => s.Id == sender.Id), eventId, now);` after the mutation. Send the join reply through `SendAsync(chat.Id, text, buttons, ct)`.
4. `SendCurrentEventAsync` sends through `SendAsync` with `Routing.SwitchButtons(store.Snapshot, entry, target?.Id, now)`.
5. Add `HandleCallbackAsync`:

```csharp
    /// <summary>
    /// A tap on one of the switch buttons. Always answered, so the button's spinner
    /// stops; only a member tapping an open event changes anything.
    /// </summary>
    private async Task HandleCallbackAsync(TgCallbackQuery callback, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = callback.From is { } from ? store.Snapshot.Senders.FirstOrDefault(s => s.Id == from.Id) : null;
        var data = callback.Data ?? "";

        if (entry is null or { Banned: true } || !data.StartsWith(Routing.CallbackPrefix, StringComparison.Ordinal))
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, null, ct);
            return;
        }

        var ev = store.Snapshot.Find(data[Routing.CallbackPrefix.Length..]);
        if (ev is null || entry.MembershipIn(ev.Id) is null)
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, "Det arrangementet er ikke tilgjengelig.", ct);
            return;
        }
        if (!ev.IsOpen(now))
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, $"{ev.Name} er avsluttet.", ct);
            return;
        }

        var switched = await store.MutateAsync(state =>
        {
            var row = state.Senders.FirstOrDefault(s => s.Id == entry.Id);
            if (row is null || row.Banned || row.MembershipIn(ev.Id) is null) return null;
            row.CurrentEventId = ev.Id;
            return row;
        }, ct);

        await telegram.AnswerCallbackQueryAsync(callback.Id, null, ct);
        if (switched is null || callback.Message?.Chat is not { } chat) return;

        await telegram.EditMessageTextAsync(chat.Id, callback.Message.MessageId,
            $"Du sender nå bilder til {ev.Name}.",
            Routing.SwitchButtons(store.Snapshot, switched, ev.Id, now), ct);
    }
```

- [ ] **Step 6: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat(telegram): inline buttons to switch between a sender's open events

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Groups are routed to events by an admin

**Files:**
- Modify: `src/EventPhotoBot/Telegram/Groups.cs`, `UpdateHandler.cs`, `src/EventPhotoBot/Web/ApiEndpoints.cs`, `src/EventPhotoBot/wwwroot/admin/telegram.html`
- Modify tests: `UpdateHandlerTests.cs`, `AdminApiTests.cs`

**Interfaces:**
- Consumes: `EventRules.IsOpen`, `Groups.Route`.
- Produces:
  - `Groups.NoticeFor(Event) → string`, `Groups.DeletionRule(Event) → string`
  - `POST /api/groups/{id}/event` with body `{ "eventId": "<id>" | "" }`, which replaces `/listening`
  - `GET /api/settings` groups carry `eventId` instead of `listening`
  - Group photos store `TelegramChatId`/`TelegramMessageId`

- [ ] **Step 1: Rewrite the group tests**

In `UpdateHandlerTests.cs`:
- Delete `The_join_code_in_a_group_starts_listening_and_posts_one_notice`, `Anything_but_the_join_code_leaves_a_group_closed` and `A_banned_member_cannot_open_a_group_with_the_code`.
- Add:

```csharp
    [Theory]
    [InlineData($"/start {Harness.JoinCode}")]
    [InlineData($"/start@eventphotobot {Harness.JoinCode}")]
    [InlineData(Harness.JoinCode)]
    public async Task Posting_the_join_code_in_a_group_does_nothing(string text)
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(new BotGroup { Id = Group, Title = "Festkomiteen" }));
        var generation = harness.Store.Generation;

        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, text)));

        Assert.Null(Assert.Single(harness.Store.Snapshot.Groups).EventId);
        Assert.Empty(harness.Telegram.Sent);
        Assert.Equal(generation, harness.Store.Generation);
    }

    [Fact]
    public async Task A_photo_in_a_group_routed_to_a_closed_event_is_ignored()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AddEvent("bryllup", closed: true);
            s.Groups.Add(new BotGroup { Id = Group, Title = "Festkomiteen", EventId = "bryllup" });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest)));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Telegram.Reactions);
    }

    [Fact]
    public async Task A_photo_in_a_group_routed_to_a_deleted_event_is_ignored()
    {
        // Review focus 4.
        var harness = await Harness.CreateAsync(s =>
            s.Groups.Add(new BotGroup { Id = Group, Title = "Festkomiteen", EventId = "gone" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest)));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
    }

    [Fact]
    public async Task A_group_photo_goes_to_the_groups_event_and_remembers_where_it_was_posted()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AddEvent("bryllup");
            s.Senders.Add(Roster(Guest));   // a daily member, not yet in the wedding
            s.Groups.Add(new BotGroup { Id = Group, Title = "Festkomiteen", EventId = "bryllup" });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest)));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal("bryllup", image.EventId);
        Assert.Equal(Group, image.TelegramChatId);
        Assert.Equal(77, image.TelegramMessageId);
        Assert.NotNull(harness.Store.Snapshot.Senders.Single().MembershipIn("bryllup"));
    }
```

In `AdminApiTests.cs`:
- `SeedGroupAsync(long id, string? eventId)` replaces the bool version.
- Replace the five `/listening` tests with:

```csharp
    [Fact]
    public async Task Settings_list_the_groups_with_their_event()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedGroupAsync(-2001, StateMigration.DefaultEventId);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var group = Assert.Single(document.RootElement.GetProperty("groups").EnumerateArray(),
            g => g.GetProperty("id").GetInt64() == -2001);

        Assert.Equal("daglig", group.GetProperty("eventId").GetString());
        Assert.Equal("<b>Festkomiteen</b>", group.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Routing_a_group_posts_a_notice_naming_the_event_once_per_change()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedGroupAsync(-2002, null);
        await _factory.Store.MutateAsync(s => { if (s.Find("r-bryllup") is null) s.AddEvent("r-bryllup", "Bryllup"); });
        var before = _factory.Telegram.Sent.Count;

        await client.PostAsJsonAsync("/api/groups/-2002/event", new { eventId = "daglig" });
        await client.PostAsJsonAsync("/api/groups/-2002/event", new { eventId = "daglig" });
        await client.PostAsJsonAsync("/api/groups/-2002/event", new { eventId = "r-bryllup" });

        var notices = _factory.Telegram.Sent.Skip(before).Where(m => m.ChatId == -2002).ToList();
        Assert.Equal(2, notices.Count);
        Assert.Contains("Daglig", notices[0].Text);
        Assert.Contains("Bryllup", notices[1].Text);
        Assert.Equal("r-bryllup", _factory.Store.Snapshot.Groups.Single(g => g.Id == -2002).EventId);
    }

    [Fact]
    public async Task Unrouting_a_group_posts_nothing()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedGroupAsync(-2003, StateMigration.DefaultEventId);
        var before = _factory.Telegram.Sent.Count;

        var response = await client.PostAsJsonAsync("/api/groups/-2003/event", new { eventId = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, _factory.Telegram.Sent.Count);
        Assert.Null(_factory.Store.Snapshot.Groups.Single(g => g.Id == -2003).EventId);
    }

    [Fact]
    public async Task Routing_an_unknown_group_or_to_an_unknown_event_is_404()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedGroupAsync(-2004, null);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/groups/-2999/event", new { eventId = "daglig" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/groups/-2004/event", new { eventId = "nope" })).StatusCode);
        Assert.DoesNotContain(_factory.Store.Snapshot.Groups, g => g.Id == -2999);
    }

    [Fact]
    public async Task A_routing_request_without_an_event_id_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedGroupAsync(-2005, StateMigration.DefaultEventId);

        var response = await client.PostAsJsonAsync("/api/groups/-2005/event", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("daglig", _factory.Store.Snapshot.Groups.Single(g => g.Id == -2005).EventId);
    }
```

  The leave tests keep their logic and use `SeedGroupAsync(id, StateMigration.DefaultEventId)`. In `Every_mutating_route_requires_a_session`, replace the `/listening` line with `await client.PostAsJsonAsync("/api/groups/-100/event", new { eventId = "daglig" }),`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~UpdateHandlerTests|FullyQualifiedName~AdminApiTests"`
Expected: FAIL. The join code still routes a group, `/event` is 404, and `TelegramChatId` is null.

- [ ] **Step 3: Implement**

`Groups.cs`: delete `ListeningNotice` and add

```csharp
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
```

`UpdateHandler.cs`:
- Delete `StartListeningAsync` and `TryReadGroupJoinCode`.
- In `HandleGroupMessageAsync`, replace the unrouted block with:

```csharp
        // Only a group an organiser has routed to an event that is open. Every other
        // group — never routed, routed to an event since deleted or closed — is
        // dropped with no reply and no write: its members did not agree to a screen.
        var group = store.Snapshot.Groups.FirstOrDefault(g => g.Id == chat.Id);
        if (store.Snapshot.Find(group?.EventId) is not { } ev || !ev.IsOpen(DateTimeOffset.UtcNow)) return;
```

  and pass `ev.Id` as `expectedEventId`.
- In `IngestAsync`'s group branch: `if (state.Find(group?.EventId) is not { } routedEvent || !routedEvent.IsOpen(now)) return (IngestOutcome.Refused, "");` and `var routed = routedEvent.Id;`.
- In the `new ImageRecord`, add `TelegramChatId = chat.IsGroup ? chat.Id : null, TelegramMessageId = chat.IsGroup ? message.MessageId : null,`.

`ApiEndpoints.cs`:
- Replace `GroupListeningRequest` with

```csharp
/// <summary>An event id routes the group, "" un-routes it; null (or missing) is a 400, so a malformed body cannot un-route a group.</summary>
public sealed record GroupEventRequest(string? EventId);
```

- Replace the `/listening` route with:

```csharp
        app.MapPost("/api/groups/{id:long}/event",
            async (long id, GroupEventRequest request, StateStore store, ITelegramClient telegram, CancellationToken ct) =>
            {
                if (request.EventId is not { } eventId)
                    return Results.BadRequest(new { error = "eventId må være en arrangement-id, eller tom for å koble fra." });
                if (eventId != "" && store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();

                // Only a group the bot is actually in. Creating a row by id here would
                // let the list claim a group the bot cannot hear.
                var (result, notice) = await store.MutateAsync(state =>
                {
                    var group = state.Groups.FirstOrDefault(g => g.Id == id);
                    if (group is null) return (Results.NotFound(), (string?)null);
                    if (eventId == "")
                    {
                        group.EventId = null;
                        return (Results.Ok(), null);
                    }
                    if (state.Find(eventId) is not { } ev) return (EventScope.UnknownEvent(), null);
                    return Groups.Route(state, id, null, DateTimeOffset.UtcNow, ev.Id)
                        ? (Results.Ok(), Groups.NoticeFor(ev))
                        : (Results.Ok(), null);
                }, ct);

                // Told in the group itself, once per change: its members did not choose this.
                if (notice is not null) await telegram.SendMessageAsync(id, notice, ct);
                return result;
            });
```

- In `GET /api/settings`, the groups projection becomes `group.Id, group.Title, group.EventId, group.FirstSeen`.

`telegram.html`:
- Replace the "Grupper" explanatory paragraph with: "Grupper boten er lagt til i. Boten henter bare bilder fra grupper som er koblet til et arrangement her. Når en gruppe kobles til et arrangement, skriver boten én melding i gruppen om at bildene kan bli vist på skjermen for det arrangementet. Medlemmer som legger ut bilder, dukker opp under «Personer»."
- In the script:

```js
    let events = [];

    async function load() {
      const [settingsResponse, eventsResponse] = await Promise.all([
        fetch('/api/settings', { cache: 'no-store' }),
        fetch('/api/events', { cache: 'no-store' }),
      ]);
      if (!settingsResponse.ok || !eventsResponse.ok) return;
      const settings = await settingsResponse.json();
      events = await eventsResponse.json();
      senders = settings.senders ?? [];
      groups = settings.groups ?? [];
      renderGroups();
      renderSenders();
    }

    const eventName = id => events.find(e => e.id === id)?.name ?? id;

    function renderGroups() {
      // Only ids go into attributes; group titles and event names are escaped into text.
      const table = document.getElementById('groups');
      table.innerHTML =
        groups.length === 0
          ? '<tr><td class="muted">Boten er ikke med i noen grupper. Legg den til i en gruppe i Telegram, så dukker gruppen opp her.</td></tr>'
          : `<tr><th>Gruppe</th><th>Bildene går til</th><th></th></tr>` +
            groups.map(group => `
            <tr>
              <td>${Admin.escapeHtml(group.title) || '<span class="muted">(uten navn)</span>'}</td>
              <td>
                <select data-id="${Number(group.id)}" aria-label="Arrangement">
                  <option value="" ${group.eventId ? '' : 'selected'}>Ikke koblet</option>
                  ${events.map(ev => `
                    <option value="${Admin.escapeHtml(ev.id)}" ${group.eventId === ev.id ? 'selected' : ''}>
                      ${Admin.escapeHtml(ev.name)}${ev.phase === 'open' ? '' : ' (' + (ev.phase === 'closed' ? 'avsluttet' : 'planlagt') + ')'}
                    </option>`).join('')}
                </select>
              </td>
              <td><button class="small quiet-danger" data-leave="${Number(group.id)}">Forlat gruppen</button></td>
            </tr>`).join('');

      for (const select of table.querySelectorAll('select'))
        select.onchange = () => setEvent(Number(select.dataset.id), select.value);
      for (const button of table.querySelectorAll('button[data-leave]'))
        button.onclick = () => leave(Number(button.dataset.leave));
    }

    async function setEvent(id, eventId) {
      const group = groups.find(g => g.id === id);
      if (eventId) {
        const sure = await Admin.confirm({
          title: `Koble ${group?.title || 'denne gruppen'} til ${eventName(eventId)}?`,
          body: 'Boten skriver en melding i gruppen om at bilder som legges ut der, kan bli vist '
              + 'på skjermen for dette arrangementet. Bilder fra gruppens medlemmer havner i køen.',
          confirmLabel: 'Koble til',
        });
        if (!sure) { renderGroups(); return; }
      }
      if (await Admin.api('POST', `/api/groups/${id}/event`, { eventId }))
        Admin.toast(eventId ? `Bildene går nå til ${eventName(eventId)}.` : 'Henter ikke lenger bilder fra gruppen.');
      await load();
    }
```

  Delete `setListening`.

Update `dev.html`'s help text under "I en gruppe" to: "…Boten ser ingenting i gruppen før den er lagt til, og henter ikke bilder før gruppen er koblet til et arrangement under Telegram." Also delete the `inGroup` `onchange` handler that relabels the join button, since posting the code in a group no longer does anything.

- [ ] **Step 4: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Check the page in the browser**

Start the local app: `preview_start` with `{ "name": "local" }` (from `.claude/launch.json`). Then:
1. Log in with password `dev`.
2. Open `/dev`, add the bot to the simulated group, then open `/admin/telegram`.
3. Choose "Daglig" in the group's dropdown and confirm.

Expected: the toast "Bildene går nå til Daglig.". Back on `/dev`, "Svar fra boten" shows the notice naming Daglig. `read_console_messages` shows no errors.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "feat(groups): an admin routes each group to an event; posting the code does nothing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Update a group photo's reaction when it is decided

**Files:**
- Create: `src/EventPhotoBot/Telegram/Reactions.cs`, `tests/EventPhotoBot.Tests/ReactionTests.cs`
- Modify: `ITelegramClient.cs`, `TelegramClient.cs`, `OfflineTelegramClient.cs`, `UpdateHandler.cs`, `ApiEndpoints.cs`, `tests/.../Fakes/FakeTelegramClient.cs`, `tests/.../TelegramClientPayloadTests.cs`

**Interfaces:**
- Consumes: `ImageRecord.TelegramChatId/MessageId` (Task 6).
- Produces:
  - `ITelegramClient.SetMessageReactionAsync(long chatId, long messageId, string? emoji, CancellationToken ct = default)`, where null clears the reaction
  - `Reactions.Queued = "👀"`, `Reactions.Live = "🔥"`, `Reactions.For(ImageStatus) → string?`
  - `Reactions.SyncAsync(ITelegramClient, ImageRecord, ILogger, CancellationToken)` and `Reactions.ClearAsync(…)`

- [ ] **Step 1: Write the tests**

Append to `TelegramClientPayloadTests.cs`:

```csharp
    [Fact]
    public async Task Clearing_a_reaction_sends_an_empty_list()
    {
        var (client, capture) = Build();

        await client.SetMessageReactionAsync(-100, 7, null);

        Assert.Equal(0, capture.Calls.Single().Body.GetProperty("reaction").GetArrayLength());
    }
```

`tests/EventPhotoBot.Tests/ReactionTests.cs`:

```csharp
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ReactionTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ReactionTests(AppFactory factory) => _factory = factory;

    private static long _nextMessage = 1000;

    private async Task<(string Id, long MessageId)> SeedGroupPhotoAsync(ImageStatus status = ImageStatus.Pending, long? chat = -4000)
    {
        var id = Guid.NewGuid().ToString("N");
        var messageId = Interlocked.Increment(ref _nextMessage);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = StateMigration.DefaultEventId, Sha256 = id, SortKey = id, Status = status,
            OriginalExtension = "jpg", SenderId = 4242, TelegramChatId = chat, TelegramMessageId = chat is null ? null : messageId,
        });
        return (id, messageId);
    }

    private (long, long, string?) LastReactionOn(long messageId) =>
        _factory.Telegram.Reactions.Last(r => r.MessageId == messageId);

    [Theory]
    [InlineData("approved", "🔥")]
    [InlineData("rejected", null)]
    [InlineData("hidden", null)]
    public async Task A_decision_sets_the_matching_reaction(string status, string? emoji)
    {
        var (id, message) = await SeedGroupPhotoAsync();

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync($"/api/images/{id}/status", new { status });

        Assert.Equal((-4000L, message, emoji), LastReactionOn(message));
    }

    [Fact]
    public async Task Deleting_a_group_photo_clears_its_reaction()
    {
        var (id, message) = await SeedGroupPhotoAsync(ImageStatus.Approved);

        await _factory.CreateAuthenticatedClient().DeleteAsync($"/api/images/{id}");

        Assert.Equal((-4000L, message, (string?)null), LastReactionOn(message));
    }

    [Fact]
    public async Task A_takeover_that_approves_a_photo_sets_fire()
    {
        var (id, message) = await SeedGroupPhotoAsync();

        await _factory.CreateAuthenticatedClient().PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 5 });

        Assert.Equal((-4000L, message, "🔥"), LastReactionOn(message));
    }

    [Fact]
    public async Task A_ban_clears_the_reactions_on_everything_they_posted()
    {
        var (_, message) = await SeedGroupPhotoAsync(ImageStatus.Approved);

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/senders/4242/status", new { status = "banned" });

        Assert.Equal((-4000L, message, (string?)null), LastReactionOn(message));
    }

    [Fact]
    public async Task A_photo_not_from_a_group_gets_no_reaction_call()
    {
        var (id, _) = await SeedGroupPhotoAsync(chat: null);
        var before = _factory.Telegram.Reactions.Count;

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync($"/api/images/{id}/status", new { status = "approved" });

        Assert.Equal(before, _factory.Telegram.Reactions.Count);
    }
}
```

(Task 8 changes the ban route; it updates this test's URL to `/api/senders/4242/ban` with `new { banned = true }`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ReactionTests|FullyQualifiedName~TelegramClientPayloadTests"`
Expected: build FAILS: `SetMessageReactionAsync` does not accept `null`.

- [ ] **Step 3: Implement**

- `ITelegramClient`: `Task SetMessageReactionAsync(long chatId, long messageId, string? emoji, CancellationToken ct = default);` and extend the summary with "Null takes the bot's reaction off."
- `TelegramClient`: `reaction = emoji is null ? Array.Empty<object>() : new object[] { new { type = "emoji", emoji } },`.
- `OfflineTelegramClient`: record `emoji ?? ""` with `Reaction: true`.
- `FakeTelegramClient`: `Reactions` becomes `List<(long ChatId, long MessageId, string? Emoji)>`.
- `src/EventPhotoBot/Telegram/Reactions.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

/// <summary>
/// The bot's reaction on a photo posted in a group: how the member learns what became
/// of it without a reply in everybody's chat. Two, so a photographer can tell "on the
/// screen" from "queued"; none once it is not going to be shown.
/// </summary>
public static class Reactions
{
    public const string Queued = "👀";
    public const string Live = "🔥";

    public static string? For(ImageStatus status) => status switch
    {
        ImageStatus.Pending => Queued,
        ImageStatus.Approved => Live,
        _ => null,
    };

    /// <summary>
    /// Brings the reaction in line with the photo's status. Best effort: the decision
    /// is already written, and a group can have left, deleted the message or banned
    /// the reaction, none of which makes the decision wrong.
    /// </summary>
    public static Task SyncAsync(ITelegramClient telegram, ImageRecord image, ILogger logger, CancellationToken ct) =>
        SetAsync(telegram, image, For(image.Status), logger, ct);

    public static Task ClearAsync(ITelegramClient telegram, ImageRecord image, ILogger logger, CancellationToken ct) =>
        SetAsync(telegram, image, null, logger, ct);

    private static async Task SetAsync(ITelegramClient telegram, ImageRecord image, string? emoji,
        ILogger logger, CancellationToken ct)
    {
        if (image.TelegramChatId is not { } chat || image.TelegramMessageId is not { } message) return;
        try
        {
            await telegram.SetMessageReactionAsync(chat, message, emoji, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not update the reaction on image {ImageId}.", image.Id);
        }
    }
}
```

- `UpdateHandler`: delete `QueuedReaction`/`LiveReaction` and use `Reactions.Live : Reactions.Queued`.
- `ApiEndpoints`: each handler below gains `ITelegramClient telegram, ILoggerFactory loggers, CancellationToken ct` parameters and uses `var log = loggers.CreateLogger("Reactions");`.
  - `POST /api/images/{id}/status`: the mutation returns the image (`(IResult Result, ImageRecord? Image)`). After it: `if (image is not null) await Reactions.SyncAsync(telegram, image, log, ct);`.
  - `PUT /api/takeover`: same, with the image the takeover approved.
  - `DELETE /api/images/{id}`: after the object deletes, `await Reactions.ClearAsync(telegram, removed, log, ct);`.
  - Ban cascade (`/api/senders/{id}/status` for now): collect the rejected images in the mutation, and after it `foreach (var image in rejected) await Reactions.SyncAsync(telegram, image, log, ct);`.
  - `DELETE /api/images` (all) is not changed. Add the comment `// Reactions are left alone on bulk deletes: hundreds of calls inside one request would meet Telegram's rate limit and the request timeout.`

- [ ] **Step 4: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(telegram): a group photo's reaction follows the organiser's decision

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Global ban, per-event auto-approve

**Files:**
- Modify: `src/EventPhotoBot/Web/ApiEndpoints.cs`, `src/EventPhotoBot/wwwroot/admin/telegram.html`, `src/EventPhotoBot/wwwroot/admin/queue.html`, `src/EventPhotoBot/wwwroot/admin/admin.css`
- Modify tests: `SenderApiTests.cs` (rewrite), `AdminApiTests.cs` (settings shape), `ReactionTests.cs` (ban route)

**Interfaces:**
- Produces:
  - `POST /api/senders/{id}/ban` with body `{ "banned": bool }`
  - `POST /api/senders/{id}/memberships/{eventId}` with body `{ "autoApprove": bool }`
  - `GET /api/settings` senders gain `banned`, `currentEventId` and `memberships: [{eventId, autoApprove}]`, and lose `status`
- Removes: `POST /api/senders/{id}/status`, `SenderStatusRequest`, `SenderStatusInput`

- [ ] **Step 1: Rewrite `SenderApiTests.cs`**

Keep `SeedImageAsync`, with an `eventId` parameter defaulting to `StateMigration.DefaultEventId`. Replace the tests with:

```csharp
    [Fact]
    public async Task Pre_approving_creates_a_row_with_an_auto_approve_membership()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5001/memberships/daglig", new { autoApprove = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sender = Assert.Single(_factory.Store.Snapshot.Senders, s => s.Id == 5001);
        Assert.False(sender.Banned);
        Assert.True(sender.MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task Auto_approve_is_per_event()
    {
        await _factory.Store.MutateAsync(s => { if (s.Find("s-bryllup") is null) s.AddEvent("s-bryllup"); });

        await Client.PostAsJsonAsync("/api/senders/5012/memberships/s-bryllup", new { autoApprove = true });

        var sender = _factory.Store.Snapshot.Senders.Single(s => s.Id == 5012);
        Assert.Null(sender.MembershipIn("daglig"));
        Assert.True(sender.MembershipIn("s-bryllup")!.AutoApprove);
    }

    [Fact]
    public async Task Turning_auto_approve_off_keeps_the_membership()
    {
        await Client.PostAsJsonAsync("/api/senders/5013/memberships/daglig", new { autoApprove = true });
        await Client.PostAsJsonAsync("/api/senders/5013/memberships/daglig", new { autoApprove = false });

        Assert.False(_factory.Store.Snapshot.Senders.Single(s => s.Id == 5013).MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task A_membership_in_an_unknown_event_is_404_and_a_missing_value_is_400()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PostAsJsonAsync("/api/senders/5014/memberships/nope", new { autoApprove = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.PostAsJsonAsync("/api/senders/5014/memberships/daglig", new { })).StatusCode);
        Assert.DoesNotContain(_factory.Store.Snapshot.Senders, s => s.Id == 5014);
    }

    [Fact]
    public async Task Banning_rejects_everything_they_sent_in_every_event()
    {
        await _factory.Store.MutateAsync(s => { if (s.Find("s-bryllup") is null) s.AddEvent("s-bryllup"); });
        var daily = await SeedImageAsync(5002, ImageStatus.Approved);
        var wedding = await SeedImageAsync(5002, ImageStatus.Pending, "s-bryllup");

        await Client.PostAsJsonAsync("/api/senders/5002/ban", new { banned = true });

        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[daily].Status);
        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[wedding].Status);
        Assert.True(_factory.Store.Snapshot.Senders.Single(s => s.Id == 5002).Banned);
    }

    [Fact]
    public async Task Banning_the_holder_of_takeover_clears_it_in_the_same_write()
    {
        var id = await SeedImageAsync(5003, ImageStatus.Approved);
        await _factory.Store.MutateAsync(s => s.Default().Settings.TakeoverImageId = id);

        await Client.PostAsJsonAsync("/api/senders/5003/ban", new { banned = true });

        Assert.Null(_factory.Store.Snapshot.Default().Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Unbanning_restores_the_memberships()
    {
        await Client.PostAsJsonAsync("/api/senders/5004/memberships/daglig", new { autoApprove = true });

        await Client.PostAsJsonAsync("/api/senders/5004/ban", new { banned = true });
        await Client.PostAsJsonAsync("/api/senders/5004/ban", new { banned = false });

        var sender = _factory.Store.Snapshot.Senders.Single(s => s.Id == 5004);
        Assert.False(sender.Banned);
        Assert.True(sender.MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task A_ban_request_without_a_value_is_rejected()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5005/ban", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_roster_routes_need_a_session()
    {
        var anonymous = _factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/senders/5006/ban", new { banned = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/senders/5006/memberships/daglig", new { autoApprove = true })).StatusCode);
    }

    [Fact]
    public async Task The_old_status_route_is_gone()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5015/status", new { status = "banned" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

Keep `The_settings_patch_cannot_write_the_roster` (seed with `/memberships/daglig` instead of `/status`) and `The_image_list_carries_the_sender_id_so_the_queue_can_ban`. Add `private HttpClient Client => _factory.CreateAuthenticatedClient();`.

In `AdminApiTests.Reading_settings_returns_the_full_shape_including_the_roster`, replace the `status` assertion with:

```csharp
        Assert.False(sender.GetProperty("banned").GetBoolean());
        var membership = Assert.Single(sender.GetProperty("memberships").EnumerateArray());
        Assert.Equal("daglig", membership.GetProperty("eventId").GetString());
        Assert.True(membership.GetProperty("autoApprove").GetBoolean());
        Assert.False(sender.TryGetProperty("status", out _));
```

In `ReactionTests.A_ban_clears_…`: `/api/senders/4242/ban` with `new { banned = true }`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~SenderApiTests|FullyQualifiedName~AdminApiTests|FullyQualifiedName~ReactionTests"`
Expected: FAIL. `/ban` and `/memberships` are 404 and `/status` is still mapped.

- [ ] **Step 3: Implement**

`ApiEndpoints.cs`:
- Delete `SenderStatusRequest`, `SenderStatusInput` and the `/status` route.
- Add the records `public sealed record BanRequest(bool? Banned);` and `public sealed record MembershipRequest(bool? AutoApprove);`.
- Add the routes:

```csharp
        app.MapPost("/api/senders/{id:long}/ban",
            async (long id, BanRequest request, StateStore store, ITelegramClient telegram,
                ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (request.Banned is not { } banned)
                    return Results.BadRequest(new { error = "banned må være true eller false." });

                var rejected = await store.MutateAsync(state =>
                {
                    var sender = state.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // Creating on write is how an organiser pre-bans a nuisance
                        // before that person has ever messaged the bot.
                        sender = new Sender { Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow };
                        state.Senders.Add(sender);
                    }

                    // Memberships are left as they are, so an unban restores them.
                    sender.Banned = banned;
                    if (!banned) return [];

                    // A ban revokes what they already sent, in every event and in this
                    // same write, so no screen can be showing a banned sender's photo
                    // between two state generations.
                    var now = DateTimeOffset.UtcNow;
                    var images = state.Images.Values.Where(i => i.SenderId == id).ToList();
                    foreach (var image in images)
                    {
                        image.Status = ImageStatus.Rejected;
                        image.DecidedAt = now;
                        ClearTakeoverIfHeldBy(state, image.Id);
                    }
                    return images;
                }, ct);

                var log = loggers.CreateLogger("Reactions");
                foreach (var image in rejected) await Reactions.SyncAsync(telegram, image, log, ct);
                return Results.Ok();
            });

        app.MapPost("/api/senders/{id:long}/memberships/{eventId}",
            async (long id, string eventId, MembershipRequest request, StateStore store) =>
            {
                if (request.AutoApprove is not { } autoApprove)
                    return Results.BadRequest(new { error = "autoApprove må være true eller false." });
                if (store.Snapshot.Find(eventId) is null) return EventScope.UnknownEvent();

                return await store.MutateAsync(state =>
                {
                    if (state.Find(eventId) is null) return EventScope.UnknownEvent();
                    var sender = state.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // How an organiser pre-approves a photographer for one event.
                        sender = new Sender { Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow };
                        state.Senders.Add(sender);
                    }
                    var membership = sender.MembershipIn(eventId);
                    if (membership is null)
                    {
                        membership = new Membership { EventId = eventId };
                        sender.Memberships.Add(membership);
                    }
                    membership.AutoApprove = autoApprove;
                    return Results.Ok();
                });
            });
```

- In `GET /api/settings`, the senders projection:

```csharp
                Senders = state.Senders.Select(sender => new
                {
                    sender.Id,
                    sender.Name,
                    sender.FirstSeen,
                    sender.Banned,
                    sender.CurrentEventId,
                    Memberships = sender.Memberships.Select(m => new { m.EventId, m.AutoApprove }),
                }),
```

- Update the `SettingsPatch` doc comment's reference to `POST /api/senders/{id}/ban`.

`queue.html` `banSender`: `Admin.api('POST', \`/api/senders/${senderId}/ban\`, { banned: true })`.

`telegram.html` "Personer":
- Replace the paragraph with: "Folk dukker opp her når de skanner en QR-kode, eller når de legger ut et bilde i en gruppe boten henter fra. For hvert arrangement de er med i, kan du la bildene deres gå rett på skjermen. «Utesteng» gjelder alle arrangementer og avviser alt de allerede har sendt."
- Replace the pre-approve form with an event select plus the id:

```html
      <label for="newId">Forhåndsgodkjenn en fotograf</label>
      <div class="row" style="margin-top: 0; flex-wrap: nowrap">
        <select id="newEvent" aria-label="Arrangement"></select>
        <input id="newId" type="number" inputmode="numeric" placeholder="Telegram-id">
        <button id="addSender" style="flex-shrink: 0">Legg til</button>
      </div>
```

- Script: replace `STATUSES`, `LABELS`, `renderSenders` and `setStatus` with:

```js
    function renderSenders() {
      // Only ids - numbers and slugs - go into attributes; names are escaped into text.
      const table = document.getElementById('senders');
      table.innerHTML =
        senders.length === 0
          ? '<tr><td class="muted">Ingen ennå.</td></tr>'
          : `<tr><th>Navn</th><th>Telegram-id</th><th>Arrangementer</th><th></th></tr>` +
            senders.map(sender => {
              const dot = sender.banned ? 'banned'
                : sender.memberships.some(m => m.autoApprove) ? 'autoApprove' : 'known';
              return `
              <tr>
                <td><span class="status-dot" data-status="${dot}"></span>${
                  Admin.escapeHtml(sender.name) || '<span class="muted">(uten navn)</span>'}</td>
                <td class="muted">${Number(sender.id)}</td>
                <td>${sender.memberships.map(m => `
                  <label class="membership">
                    <input type="checkbox" data-sender="${Number(sender.id)}" data-event="${Admin.escapeHtml(m.eventId)}"
                           ${m.autoApprove ? 'checked' : ''} ${sender.banned ? 'disabled' : ''}>
                    ${Admin.escapeHtml(eventName(m.eventId))}: godkjenn automatisk
                  </label>`).join('') || '<span class="muted">Ingen</span>'}</td>
                <td><button class="small ${sender.banned ? '' : 'quiet-danger'}" data-ban="${Number(sender.id)}">
                  ${sender.banned ? 'Opphev utestengning' : 'Utesteng…'}</button></td>
              </tr>`;
            }).join('');

      for (const box of table.querySelectorAll('input[data-sender]'))
        box.onchange = () => setAutoApprove(Number(box.dataset.sender), box.dataset.event, box.checked);
      for (const button of table.querySelectorAll('button[data-ban]'))
        button.onclick = () => toggleBan(Number(button.dataset.ban));

      const select = document.getElementById('newEvent');
      select.innerHTML = events.filter(e => e.phase !== 'closed')
        .map(e => `<option value="${Admin.escapeHtml(e.id)}">${Admin.escapeHtml(e.name)}</option>`).join('');
    }

    async function setAutoApprove(id, eventId, autoApprove) {
      const sender = senders.find(s => s.id === id);
      if (await Admin.api('POST', `/api/senders/${id}/memberships/${encodeURIComponent(eventId)}`, { autoApprove }))
        Admin.toast(`${sender?.name || id}: ${autoApprove ? 'rett på skjermen' : 'til godkjenning'} i ${eventName(eventId)}.`);
      await load();
    }

    async function toggleBan(id) {
      const sender = senders.find(s => s.id === id);
      const banned = !sender?.banned;
      if (banned) {
        const sure = await Admin.confirm({
          title: `Utestenge ${sender?.name || 'denne personen'}?`,
          body: 'Gjelder alle arrangementer. Alt vedkommende allerede har sendt blir avvist, også '
              + 'bilder som vises på skjermen nå.',
          confirmLabel: 'Utesteng',
          danger: true,
        });
        if (!sure) return;
      }
      if (await Admin.api('POST', `/api/senders/${id}/ban`, { banned }))
        Admin.toast(banned ? `${sender?.name || id} er utestengt.` : `${sender?.name || id} er ikke lenger utestengt.`);
      await load();
    }
```

  `addSender` posts to `/api/senders/${id}/memberships/${encodeURIComponent(document.getElementById('newEvent').value)}` with `{ autoApprove: true }`.

`admin.css`: add

```css
.membership { display: flex; gap: 6px; align-items: center; font-size: 0.9em; margin: 2px 0; }
```

- [ ] **Step 4: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Check the page in the browser**

With the `local` preview running:
1. On `/dev`, join as a guest.
2. On `/admin/telegram`, tick "Daglig: godkjenn automatisk", then ban and unban.

Expected: toasts appear and the tick survives the unban. `read_console_messages` shows no errors.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "feat(roster): global ban and per-event auto-approve replace the status field

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Retention: the sweep, its trigger, and its settings

**Files:**
- Create: `src/EventPhotoBot/State/RetentionSweep.cs`, `src/EventPhotoBot/Web/RetentionEndpoints.cs`, `tests/EventPhotoBot.Tests/RetentionSweepTests.cs`, `tests/EventPhotoBot.Tests/RetentionEndpointTests.cs`
- Modify: `src/EventPhotoBot/AppConfig.cs`, `src/EventPhotoBot/Web/AuthEndpoints.cs`, `src/EventPhotoBot/Web/ApiEndpoints.cs` (settings GET carries retention), `src/EventPhotoBot/Program.cs`, `src/EventPhotoBot/wwwroot/admin/settings.html`, `src/EventPhotoBot/DevTools/dev.html`, `infra/main.tf`, `infra/deploy.ps1`, `tests/EventPhotoBot.Tests/AppFactory.cs`, `tests/EventPhotoBot.Tests/AppConfigTests.cs`

**Interfaces:**
- Produces:
  - `RetentionSweep.Select(EventState, Event, DateTimeOffset) → IReadOnlyList<ImageRecord>`
  - `RetentionSweep.RunAsync(StateStore, IObjectStore, ILogger, string? onlyEventId, DateTimeOffset now, CancellationToken) → IReadOnlyDictionary<string, int>`
  - `PUT /api/events/{id}/retention` with body `{ "maxAgeDays": int|null, "keepNewest": int|null }`
  - `POST /api/events/{id}/retention/run`
  - `POST /internal/retention` with header `X-Retention-Secret`
  - `AppConfig.RetentionSecret` (optional `RETENTION_SECRET`)
  - `AppFactory.RetentionSecret`, which is settable before first use and defaults to `"retention-secret"`

- [ ] **Step 1: Write the sweep tests**

`tests/EventPhotoBot.Tests/RetentionSweepTests.cs`:

```csharp
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class RetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 3, 0, 0, TimeSpan.Zero);

    private static EventState With(int? maxAgeDays, int keepNewest,
        params (string Id, int AgeDays, ImageStatus Status, PinKind Pin)[] images)
    {
        var state = TestState.New();
        state.Default().Retention = new Retention { MaxAgeDays = maxAgeDays, KeepNewest = keepNewest };
        foreach (var (id, age, status, pin) in images)
            state.Images[id] = new ImageRecord
            {
                Id = id, EventId = "daglig", Sha256 = id, SortKey = id, OriginalExtension = "jpg",
                Status = status, Pin = pin, ReceivedAt = Now.AddDays(-age),
            };
        return state;
    }

    private static string[] Selected(EventState state) =>
        [.. RetentionSweep.Select(state, state.Default(), Now).Select(i => i.Id).Order()];

    [Fact]
    public void Off_deletes_nothing() =>
        Assert.Empty(Selected(With(null, 0, ("old", 400, ImageStatus.Approved, PinKind.None))));

    [Fact]
    public void Everything_older_than_the_limit_goes_whatever_its_status()
    {
        var state = With(30, 0,
            ("new", 1, ImageStatus.Approved, PinKind.None),
            ("oldA", 31, ImageStatus.Approved, PinKind.None),
            ("oldP", 31, ImageStatus.Pending, PinKind.None),
            ("oldR", 31, ImageStatus.Rejected, PinKind.None));

        Assert.Equal(["oldA", "oldP", "oldR"], Selected(state));
    }

    [Fact]
    public void The_newest_approved_are_kept_as_a_pool_and_only_approved_count()
    {
        var state = With(30, 2,
            ("a40", 40, ImageStatus.Approved, PinKind.None),
            ("a35", 35, ImageStatus.Approved, PinKind.None),
            ("p33", 33, ImageStatus.Pending, PinKind.None),
            ("a50", 50, ImageStatus.Approved, PinKind.None));

        // a35 and a40 are the two newest approved; the pending one does not count toward K.
        Assert.Equal(["a50", "p33"], Selected(state));
    }

    [Fact]
    public void Pinned_and_takeover_photos_are_never_swept()
    {
        var state = With(30, 0,
            ("pin", 90, ImageStatus.Approved, PinKind.Recurring),
            ("held", 90, ImageStatus.Approved, PinKind.None),
            ("gone", 90, ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "held";

        Assert.Equal(["gone"], Selected(state));
    }

    [Fact]
    public void Another_events_photos_are_not_touched()
    {
        var state = With(30, 0);
        state.AddEvent("bryllup");
        state.Images["w"] = new ImageRecord
        {
            Id = "w", EventId = "bryllup", Sha256 = "w", SortKey = "w", OriginalExtension = "jpg",
            ReceivedAt = Now.AddDays(-400),
        };

        Assert.Empty(Selected(state));
    }

    [Fact]
    public async Task Running_writes_state_once_then_deletes_the_objects()
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(s =>
        {
            s.Default().Retention = new Retention { MaxAgeDays = 30 };
            s.Images["old"] = new ImageRecord
            {
                Id = "old", EventId = "daglig", Sha256 = "o", SortKey = "o", OriginalExtension = "jpg",
                ReceivedAt = Now.AddDays(-60),
            };
        });
        await objects.WriteAsync(ObjectPaths.Original("old", "jpg"), [1], "image/jpeg", null);
        var generation = store.Generation;

        var counts = await RetentionSweep.RunAsync(store, objects, NullLogger.Instance, null, Now, default);

        Assert.Equal(1, counts["daglig"]);
        Assert.Equal(generation + 1, store.Generation);
        Assert.Empty(store.Snapshot.Images);
        Assert.DoesNotContain(ObjectPaths.Original("old", "jpg"), objects.Paths);
    }

    [Fact]
    public async Task Running_with_nothing_to_do_does_not_write()
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(s => s.Default().Retention = new Retention { MaxAgeDays = 30 });
        var generation = store.Generation;

        await RetentionSweep.RunAsync(store, objects, NullLogger.Instance, null, Now, default);

        Assert.Equal(generation, store.Generation);
    }
}
```

`tests/EventPhotoBot.Tests/RetentionEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class RetentionEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public RetentionEndpointTests(AppFactory factory) => _factory = factory;

    private HttpRequestMessage Sweep(string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/retention");
        if (secret is not null) request.Headers.Add("X-Retention-Secret", secret);
        return request;
    }

    [Fact]
    public async Task The_sweep_needs_the_secret_but_no_session()
    {
        var client = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Sweep(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Sweep("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Sweep(AppFactory.DefaultRetentionSecret))).StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_secret_the_sweep_route_does_not_exist()
    {
        using var factory = new AppFactory { RetentionSecret = null };

        var response = await factory.CreateAnonymousClient().SendAsync(Sweep("anything"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retention_is_set_per_event_and_validated()
    {
        var client = _factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = 30, keepNewest = 50 })).StatusCode);
        Assert.Equal(30, _factory.Store.Snapshot.Default().Retention.MaxAgeDays);
        Assert.Equal(50, _factory.Store.Snapshot.Default().Retention.KeepNewest);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = 0, keepNewest = 0 })).StatusCode);

        await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = (int?)null, keepNewest = (int?)null });
        Assert.Null(_factory.Store.Snapshot.Default().Retention.MaxAgeDays);
    }

    [Fact]
    public async Task Rydd_na_runs_the_sweep_for_one_event()
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.Store.MutateAsync(s =>
        {
            s.Default().Retention = new Retention { MaxAgeDays = 1 };
            s.Images["ancient"] = new ImageRecord
            {
                Id = "ancient", EventId = "daglig", Sha256 = "a", SortKey = "a", OriginalExtension = "jpg",
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
            };
        });

        var response = await client.PostAsync("/api/events/daglig/retention/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("ancient", _factory.Store.Snapshot.Images.Keys);
    }
}
```

In `AppFactory.cs`:

```csharp
    public const string DefaultRetentionSecret = "retention-secret";

    /// <summary>Set before the first client is created; null leaves RETENTION_SECRET unset.</summary>
    public string? RetentionSecret { get; init; } = DefaultRetentionSecret;
```

and in `ConfigureWebHost`: `if (RetentionSecret is not null) builder.UseSetting("RETENTION_SECRET", RetentionSecret);`.

In `AppConfigTests.cs`, add:

```csharp
    [Fact]
    public void The_retention_secret_is_optional_and_trimmed()
    {
        Assert.Null(AppConfig.Load(Config(Complete())).RetentionSecret);
        var pairs = Complete().Append(("RETENTION_SECRET", " s3cret\n")).ToArray();
        Assert.Equal("s3cret", AppConfig.Load(Config(pairs)).RetentionSecret);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~Retention|FullyQualifiedName~AppConfigTests"`
Expected: build FAILS: `RetentionSweep` and `RetentionSecret` are not defined.

- [ ] **Step 3: Implement the sweep**

`src/EventPhotoBot/State/RetentionSweep.cs`:

```csharp
namespace EventPhotoBot.State;

public static class RetentionSweep
{
    /// <summary>
    /// The photos retention would delete from one event now. Kept: everything received
    /// within MaxAgeDays, the KeepNewest newest approved (so the screen always has a
    /// pool), pinned photos, and the one holding the screen.
    /// </summary>
    public static IReadOnlyList<ImageRecord> Select(EventState state, Event ev, DateTimeOffset now)
    {
        if (ev.Retention.MaxAgeDays is not { } days) return [];
        var cutoff = now.AddDays(-days);
        var images = state.Images.Values.Where(i => i.EventId == ev.Id).ToList();

        var keep = images
            .Where(i => i.Status == ImageStatus.Approved)
            .OrderByDescending(i => i.ReceivedAt)
            .ThenByDescending(i => i.SortKey, StringComparer.Ordinal)
            .Take(ev.Retention.KeepNewest)
            .Select(i => i.Id)
            .ToHashSet();
        if (ev.Settings.TakeoverImageId is { } held) keep.Add(held);

        return [.. images.Where(i => i.ReceivedAt < cutoff && i.Pin != PinKind.Recurring && !keep.Contains(i.Id))];
    }

    /// <summary>
    /// Every event with retention on, or just <paramref name="onlyEventId"/>. One state
    /// write for the lot, then the objects — the same order as every other delete.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> RunAsync(StateStore store, IObjectStore objects,
        ILogger logger, string? onlyEventId, DateTimeOffset now, CancellationToken ct)
    {
        bool InScope(Event e) => onlyEventId is null || e.Id == onlyEventId;

        // MutateAsync always writes; a nightly no-op must not bump every screen's ETag.
        if (!store.Snapshot.Events.Where(InScope).Any(e => Select(store.Snapshot, e, now).Count > 0))
            return new Dictionary<string, int>();

        var removed = await store.MutateAsync(state =>
        {
            var gone = new List<ImageRecord>();
            foreach (var ev in state.Events.Where(InScope))
                foreach (var image in Select(state, ev, now))
                {
                    state.Images.Remove(image.Id);
                    gone.Add(image);
                }
            return gone;
        }, ct);

        foreach (var image in removed) await ImageObjects.DeleteAsync(objects, image, ct);

        var counts = removed.GroupBy(i => i.EventId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (eventId, count) in counts)
            logger.LogInformation("Retention removed {Count} images from event {EventId}.", count, eventId);
        return counts;
    }
}
```

- [ ] **Step 4: Implement the config, the routes and the page**

`AppConfig.cs`:

```csharp
    /// <summary>
    /// Optional. The header Cloud Scheduler sends to /internal/retention. Unset, the
    /// route answers 404 and photos are only removed by hand.
    /// </summary>
    public string? RetentionSecret { get; init; }
```

In `Load`: `RetentionSecret = string.IsNullOrWhiteSpace(config["RETENTION_SECRET"]) ? null : config["RETENTION_SECRET"]!.Trim(),`. In `LogLoaded`: `if (RetentionSecret is not null) logger.LogInformation("Secret {Key} loaded.", "RETENTION_SECRET");`.

`AuthEndpoints.UseSessionGate`: add `"/internal/retention"` to `open`, and extend the doc comment: "…the login surface and the retention trigger, which has its own secret, need a session".

`src/EventPhotoBot/Web/RetentionEndpoints.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record RetentionRequest(int? MaxAgeDays, int? KeepNewest);

public static class RetentionEndpoints
{
    public static void MapRetention(this WebApplication app)
    {
        // Called once a day by Cloud Scheduler. Outside the session gate, so it carries
        // its own secret; with none configured the route does not exist at all.
        app.MapPost("/internal/retention",
            async (HttpContext http, AppConfig config, StateStore store, IObjectStore objects,
                ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (config.RetentionSecret is not { } secret) return Results.NotFound();
                if (!SecretComparison.Matches(secret, http.Request.Headers["X-Retention-Secret"]))
                    return Results.Unauthorized();

                var counts = await RetentionSweep.RunAsync(store, objects,
                    loggers.CreateLogger("Retention"), null, DateTimeOffset.UtcNow, ct);
                return Results.Ok(new { deleted = counts });
            });

        app.MapPut("/api/events/{id}/retention", async (string id, RetentionRequest request, StateStore store) =>
        {
            if (request.MaxAgeDays is { } days && days is < 1 or > 3650)
                return EventEndpoints.BadRequest("maxAgeDays må være mellom 1 og 3650, eller tom.");
            var keep = Math.Clamp(request.KeepNewest ?? 0, 0, 10_000);
            if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();

            return await store.MutateAsync(state =>
            {
                if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
                ev.Retention = new Retention { MaxAgeDays = request.MaxAgeDays, KeepNewest = keep };
                return Results.Ok();
            });
        });

        app.MapPost("/api/events/{id}/retention/run",
            async (string id, StateStore store, IObjectStore objects, ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (store.Snapshot.Find(id) is null) return EventScope.UnknownEvent();
                var counts = await RetentionSweep.RunAsync(store, objects,
                    loggers.CreateLogger("Retention"), id, DateTimeOffset.UtcNow, ct);
                return Results.Ok(new { deleted = counts.GetValueOrDefault(id) });
            });
    }
}
```

`Program.cs`: `app.MapRetention();` after `app.MapEvents();`.

`ApiEndpoints` `GET /api/settings`: add `Retention = new { ev.Retention.MaxAgeDays, ev.Retention.KeepNewest },`.

`settings.html`: add, after the "Bildevisning" card:

```html
    <section class="card">
      <h2>Sletting av gamle bilder</h2>
      <label class="toggle"><input id="retentionOn" type="checkbox">
        <span>Slett gamle bilder automatisk</span>
        <span class="muted">Én gang i døgnet. Festede bilder og bildet som holder skjermen, slettes aldri.</span></label>
      <div class="number-fields">
        <div>
          <label for="maxAgeDays">Slett bilder eldre enn (dager)</label>
          <input id="maxAgeDays" type="number" min="1" max="3650" inputmode="numeric">
        </div>
        <div>
          <label for="keepNewest">Behold alltid de nyeste godkjente (antall)</label>
          <input id="keepNewest" type="number" min="0" max="10000" inputmode="numeric">
        </div>
      </div>
      <div class="save-row">
        <button class="primary" id="saveRetention">Lagre</button>
        <button id="runRetention">Rydd nå</button>
        <span class="saved" id="savedRetention">Lagret</span>
      </div>
    </section>
```

and in the script's `load()`, after the other fields:

```js
      const retention = settings.retention ?? {};
      for (const [key, value] of [['maxAgeDays', retention.maxAgeDays ?? 30], ['keepNewest', retention.keepNewest ?? 0]]) {
        const input = document.getElementById(key);
        if (!keep(input)) input.value = value;
      }
      const on = document.getElementById('retentionOn');
      if (!keep(on)) on.checked = retention.maxAgeDays != null;
```

with the handlers:

```js
    const retentionPath = () => `/api/events/${encodeURIComponent(settings.eventId)}/retention`;

    document.getElementById('saveRetention').onclick = async () => {
      const on = document.getElementById('retentionOn').checked;
      if (await Admin.api('PUT', retentionPath(), {
        maxAgeDays: on ? Number(document.getElementById('maxAgeDays').value) : null,
        keepNewest: Number(document.getElementById('keepNewest').value),
      })) {
        for (const key of ['retentionOn', 'maxAgeDays', 'keepNewest']) edited.delete(key);
        flashSaved('savedRetention');
      }
      load();
    };

    document.getElementById('runRetention').onclick = async () => {
      const sure = await Admin.confirm({
        title: 'Slette gamle bilder nå?',
        body: 'Bilder eldre enn grensen slettes fra skjermen og lagringen, bortsett fra de nyeste '
            + 'du har valgt å beholde, festede bilder og bildet som holder skjermen. Dette kan ikke angres.',
        confirmLabel: 'Rydd nå',
        danger: true,
      });
      if (!sure) return;
      const response = await Admin.api('POST', `${retentionPath()}/run`);
      if (response) {
        const { deleted } = await response.json();
        Admin.toast(deleted === 1 ? 'Slettet 1 bilde.' : `Slettet ${deleted} bilder.`);
      }
    };
```

`dev.html`: nothing to add. "Rydd nå" is on the settings page, and it runs the same code the scheduler does.

- [ ] **Step 5: Infrastructure**

`infra/main.tf`:
- Add `"cloudscheduler.googleapis.com",` to `local.apis`.
- Add `retention_secret = "${var.name}-retention-secret"` to `local.secret_ids`.
- Add `RETENTION_SECRET = local.secret_ids.retention_secret` to the env `for_each` map.
- Delete the `lifecycle_rule { … }` block on `google_storage_bucket.images`, and replace the comment after the bucket with:

```hcl
# No lifecycle rule. Photos are kept until an organiser deletes them or their
# event's retention setting does (see RetentionSweep); an age rule here would
# delete the bytes of photos the state still lists, including the pool the
# daily screen is told to keep.
```

- Change `timeout = "120s"` to `timeout = "900s"`, and its comment to `# A 20 MB getFile plus derivatives needs seconds; an event's ZIP export can need minutes.`

`infra/deploy.ps1`:
- Add a param `[string] $SchedulerRegion = 'europe-west1'`, commented: Cloud Scheduler is not offered in every region; check with `gcloud scheduler locations list`.
- Add the secret to the printed list: `printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-retention-secret --data-file=- --project $ProjectId`, and include `retention-secret` in the "Generate the random ones … openssl rand -hex 32" line.
- After the webhook registration, before the final summary, add:

```powershell
Write-Host '==> Scheduling the daily retention sweep' -ForegroundColor Cyan
$retentionSecret = (gcloud secrets versions access latest --secret "$Name-retention-secret" --project $ProjectId | Out-String).Trim()
Assert-Success 'gcloud secrets versions access (retention-secret)'
if ([string]::IsNullOrWhiteSpace($retentionSecret)) { throw 'retention-secret has no version yet. Add it (see above) and rerun.' }

# Created with gcloud, not Terraform, for the same reason as every secret value here:
# the header would otherwise sit in plaintext in Terraform state.
$job = "$Name-retention"
$jobArgs = @('--location', $SchedulerRegion, '--project', $ProjectId,
    '--schedule', '15 3 * * *', '--time-zone', 'Europe/Oslo',
    '--uri', "$serviceUrl/internal/retention", '--http-method', 'POST')
gcloud scheduler jobs describe $job --location $SchedulerRegion --project $ProjectId *> $null
if ($LASTEXITCODE -eq 0) {
    gcloud scheduler jobs update http $job @jobArgs --update-headers "X-Retention-Secret=$retentionSecret"
} else {
    gcloud scheduler jobs create http $job @jobArgs --headers "X-Retention-Secret=$retentionSecret"
}
Assert-Success 'gcloud scheduler jobs create/update'
```

Before relying on the flags, confirm them with `gcloud scheduler jobs create http --help`. The flags used here are `--headers` on create and `--update-headers` on update.

- [ ] **Step 6: Run the suite and validate the Terraform**

Run: `dotnet test`
Expected: PASS.

Run: `terraform -chdir=infra fmt -check` and `terraform -chdir=infra validate` (after `terraform -chdir=infra init -backend=false`).
Expected: no formatting diff, and "Success! The configuration is valid."

- [ ] **Step 7: Commit**

```bash
git add -A src tests infra
git commit -m "feat(retention): a daily sweep of old photos, kept pool and all, and no bucket age rule

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: ZIP export of an event's approved originals

**Files:**
- Modify: `src/EventPhotoBot/Web/EventEndpoints.cs`
- Create: `tests/EventPhotoBot.Tests/ExportTests.cs`

**Interfaces:**
- Produces: `GET /api/events/{id}/export.zip`

- [ ] **Step 1: Write the test**

`tests/EventPhotoBot.Tests/ExportTests.cs`:

```csharp
using System.IO.Compression;
using System.Net;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ExportTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ExportTests(AppFactory factory) => _factory = factory;

    private async Task SeedAsync(string id, ImageStatus status, byte[] original, DateTimeOffset at)
    {
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), original, "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = "daglig", Sha256 = id, SortKey = id, Status = status,
            OriginalExtension = "jpg", ReceivedAt = at,
        });
    }

    [Fact]
    public async Task The_zip_holds_the_approved_originals_only()
    {
        var at = new DateTimeOffset(2026, 9, 23, 18, 5, 9, TimeSpan.Zero);
        await SeedAsync("EXP1", ImageStatus.Approved, [1, 2, 3], at);
        await SeedAsync("EXP2", ImageStatus.Pending, [4], at);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/events/daglig/export.zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var entry = Assert.Single(zip.Entries, e => e.Name.Contains("EXP"));
        Assert.Equal("20260923-180509Z-EXP1.jpg", entry.Name);
        using var content = new MemoryStream();
        await entry.Open().CopyToAsync(content);
        Assert.Equal([1, 2, 3], content.ToArray());
    }

    [Fact]
    public async Task An_unknown_event_is_404_and_a_session_is_required()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateAuthenticatedClient().GetAsync("/api/events/nope/export.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateAnonymousClient().GetAsync("/api/events/daglig/export.zip")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ExportTests"`
Expected: FAIL with 404 on the first request.

- [ ] **Step 3: Implement**

In `EventEndpoints.MapEvents` (add `using System.IO.Compression;`):

```csharp
        // Streamed straight into the response, entry by entry: an event's originals
        // can run to gigabytes, and the instance has 1 GiB of memory, /tmp included.
        app.MapGet("/api/events/{id}/export.zip",
            async (string id, HttpContext http, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                var state = store.Snapshot;
                if (state.Find(id) is not { } ev) return EventScope.UnknownEvent();
                var images = state.Images.Values
                    .Where(i => i.EventId == ev.Id && i.Status == ImageStatus.Approved)
                    .OrderBy(i => i.ReceivedAt)
                    .ToList();

                http.Response.ContentType = "application/zip";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{ev.Id}.zip\"";

                await using (var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create,
                                 leaveOpen: true, entryNameEncoding: null, ct))
                {
                    foreach (var image in images)
                    {
                        await using var source = await objects.OpenReadAsync(
                            ObjectPaths.Original(image.Id, image.OriginalExtension), ct);
                        if (source is null) continue;   // bytes already gone; the rest are still worth having

                        // UTC, marked as such: the container may carry no time-zone data.
                        var name = $"{image.ReceivedAt.UtcDateTime:yyyyMMdd-HHmmss}Z-{image.Id}.{image.OriginalExtension}";
                        // Photos are already compressed; deflating them again costs CPU for nothing.
                        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        await using var target = await entry.OpenAsync(ct);
                        await source.CopyToAsync(target, ct);
                    }
                }

                return Results.Empty;
            });
```

If `ZipArchive.CreateAsync` or `ZipArchiveEntry.OpenAsync` does not exist in the installed .NET 10 SDK, use the synchronous forms instead. They need `http.Features.Get<IHttpBodyControlFeature>()` with `AllowSynchronousIO = true` set before writing, and the test host must allow synchronous IO too. Say so in the commit message if you take that path.

- [ ] **Step 4: Run the suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(events): download an event's approved originals as a ZIP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Admin UI: event picker, the events page, and scoped pages

No automated tests cover the HTML pages. These were verified in the browser.

**Files:**
- Create: `src/EventPhotoBot/wwwroot/admin/events.html`
- Modify: `admin.js`, `admin.css`, `queue.html`, `images.html`, `settings.html`, `upload.html`, `telegram.html` (nav only), `DevTools/dev.html` (nav only), `src/EventPhotoBot/Program.cs`, `tests/EventPhotoBot.Tests/EndpointAuthTests.cs`

**Interfaces:**
- Consumes: `GET /api/events` (Task 2), the `?event=` scoping (Task 3), `PUT …/schedule`, `…/close`, `…/reopen`, `…/rotate-code`, `DELETE /api/events/{id}`, `…/export.zip`.
- Produces (in `window.Admin`):
  - `Admin.events(fresh = false) → Promise<Event[]>`
  - `Admin.currentEvent() → Promise<Event>`
  - `Admin.withEvent(path) → string`, which adds `event=` only for a non-default selection
  - `Admin.eventId`, which is the `?event=` value or null

- [ ] **Step 1: Route and auth test**

`Program.cs`: `app.MapGet("/admin/events", () => Results.File(Path.Combine(app.Environment.WebRootPath, "admin", "events.html"), "text/html"));`

`EndpointAuthTests.Pages_redirect_to_login_without_a_session`: add `[InlineData("/admin/events")]`.

Run: `dotnet test --filter "FullyQualifiedName~EndpointAuthTests"`
Expected: PASS once the route and the page exist. Create the page file in Step 3 before running.

- [ ] **Step 2: `admin.js`**

Add inside the IIFE, above `window.Admin = …`:

```js
  // ---- event context -------------------------------------------------------
  // The event a page is about lives in ?event=; absent means the default event,
  // which is what every page and bookmark from before events already meant.
  const eventId = new URLSearchParams(location.search).get('event');
  let eventsPromise = null;
  const PHASES = { open: 'åpent', scheduled: 'planlagt', closed: 'avsluttet' };

  function events(fresh = false) {
    if (fresh || !eventsPromise) {
      eventsPromise = fetch('/api/events', { cache: 'no-store' })
        .then(response => response.ok ? response.json() : [])
        .catch(() => []);
    }
    return eventsPromise;
  }

  async function currentEvent() {
    const list = await events();
    return list.find(e => e.id === eventId) ?? list.find(e => e.isDefault) ?? null;
  }

  function withEvent(path) {
    if (!eventId) return path;
    return `${path}${path.includes('?') ? '&' : '?'}event=${encodeURIComponent(eventId)}`;
  }

  async function renderEventPicker() {
    const nav = document.querySelector('nav');
    if (!nav) return;
    const list = await events();
    if (list.length < 2) return;   // only the daily event: nothing to choose between
    const current = await currentEvent();

    const select = document.createElement('select');
    select.className = 'event-picker';
    select.setAttribute('aria-label', 'Arrangement');
    for (const ev of list) {
      const option = document.createElement('option');
      option.value = ev.id;
      // textContent, not innerHTML: an event name is typed by an organiser, but it
      // is still text, and this keeps it that way.
      option.textContent = ev.phase === 'open' ? ev.name : `${ev.name} (${PHASES[ev.phase]})`;
      option.selected = ev.id === current?.id;
      select.appendChild(option);
    }
    select.onchange = () => {
      const next = new URL(location.href);
      const chosen = list.find(e => e.id === select.value);
      if (chosen?.isDefault) next.searchParams.delete('event');
      else next.searchParams.set('event', select.value);
      location.href = next.toString();
    };
    nav.insertBefore(select, nav.querySelector('.nav-logout'));
  }
```

Add to `window.Admin`: `events, currentEvent, withEvent, eventId,`.

Replace the nav loop at the bottom with:

```js
  // Pages about one event carry the choice along; Telegram and the events list are
  // about every event, so their links stay plain.
  const GLOBAL_PAGES = ['/admin/telegram', '/admin/events', '/dev'];
  for (const link of document.querySelectorAll('nav a')) {
    const href = link.getAttribute('href');
    if (href === location.pathname) link.classList.add('active');
    if (!GLOBAL_PAGES.includes(href)) link.href = withEvent(href);
  }

  renderEventPicker();
```

In `pollOnce`: `fetch(withEvent('/api/manifest'), …)`, and the badge uses `manifest.pendingTotal`. The badge counts everything waiting, across events, because the queue shows all of it.

In `renderTakeoverBanner`: `clear.onclick = () => api('DELETE', withEvent('/api/takeover'));`.

`admin.css`:

```css
nav .event-picker { margin-left: auto; max-width: 14em; font-size: 0.9em; }
.event-label { display: inline-block; font-size: 0.8em; padding: 1px 6px; border-radius: 4px; background: var(--surface-2, #eee); }
```

- [ ] **Step 3: Nav links and the events page**

In the `<nav>` of every admin page (`queue.html`, `images.html`, `settings.html`, `telegram.html`, `upload.html`, `DevTools/dev.html`), add `<a href="/admin/events">Arrangementer</a>` after the Telegram link, and add `events.html` with the same nav.

Create `src/EventPhotoBot/wwwroot/admin/events.html`:

```html
<!doctype html>
<html lang="nb">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Arrangementer</title>
  <link rel="stylesheet" href="/admin/admin.css">
</head>
<body>
  <nav>
    <a href="/admin/queue">Kø<span class="badge"></span></a>
    <a href="/admin/images">Bilder</a>
    <a href="/upload">Last opp</a>
    <a href="/admin/telegram">Telegram</a>
    <a href="/admin/events">Arrangementer</a>
    <a href="/admin/settings">Innstillinger</a>
    <a href="/show">Skjerm</a>
    <form method="post" action="/logout" class="nav-logout"><button type="submit">Logg ut</button></form>
  </nav>

  <main class="narrow">
    <header class="page-head">
      <h1>Arrangementer</h1>
      <p class="muted">
        «Daglig» er kirkens faste skjerm. Et bryllup eller en konsert får sitt eget
        arrangement, med egen QR-kode, egen skjerm og egne bilder.
      </p>
    </header>

    <section class="card">
      <h2>Nytt arrangement</h2>
      <label for="newName">Navn</label>
      <input id="newName" type="text" maxlength="100" placeholder="Bryllup Kari og Ola">
      <label for="newId">Kort id (i lenken til skjermen)</label>
      <input id="newId" type="text" maxlength="32" pattern="[a-z0-9-]{1,32}" placeholder="bryllup-kari-ola">
      <div class="number-fields">
        <div><label for="newOpens">Åpner (valgfritt)</label><input id="newOpens" type="datetime-local"></div>
        <div><label for="newCloses">Stenger (valgfritt)</label><input id="newCloses" type="datetime-local"></div>
      </div>
      <div class="save-row"><button class="primary" id="create">Opprett</button></div>
    </section>

    <div id="lists"></div>
  </main>

  <script src="/admin/admin.js"></script>
  <script>
    'use strict';

    const GROUPS = [['open', 'Åpne'], ['scheduled', 'Planlagte'], ['closed', 'Avsluttede']];

    // datetime-local has no zone: the browser reads it as local time, which for this
    // app's organisers is Europe/Oslo, and toISOString() turns that into UTC.
    const toIso = value => value ? new Date(value).toISOString() : null;
    const toLocalInput = iso => {
      if (!iso) return '';
      const d = new Date(iso);
      const pad = n => String(n).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    };
    const when = iso => iso ? new Date(iso).toLocaleString('nb-NO', { dateStyle: 'medium', timeStyle: 'short' }) : '';

    // Suggest an id from the name until the organiser types one of their own.
    let idTouched = false;
    document.getElementById('newId').addEventListener('input', () => { idTouched = true; });
    document.getElementById('newName').addEventListener('input', event => {
      if (idTouched) return;
      document.getElementById('newId').value = event.target.value.toLowerCase()
        .replace(/æ/g, 'ae').replace(/ø/g, 'o').replace(/å/g, 'a')
        .normalize('NFD').replace(/[̀-ͯ]/g, '')
        .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32);
    });

    document.getElementById('create').onclick = async () => {
      const body = {
        id: document.getElementById('newId').value.trim(),
        name: document.getElementById('newName').value,
        opensAt: toIso(document.getElementById('newOpens').value),
        closesAt: toIso(document.getElementById('newCloses').value),
      };
      if (await Admin.api('POST', '/api/events', body)) {
        Admin.toast(`${body.name.trim()} er opprettet.`);
        for (const id of ['newName', 'newId', 'newOpens', 'newCloses']) document.getElementById(id).value = '';
        idTouched = false;
      }
      load();
    };

    async function load() {
      const list = await Admin.events(true);
      const host = document.getElementById('lists');
      host.replaceChildren(...GROUPS.map(([phase, title]) => {
        const items = list.filter(e => e.phase === phase);
        const section = document.createElement('section');
        section.className = 'card';
        section.innerHTML = `<h2>${title}</h2>` + (items.length === 0
          ? '<p class="muted">Ingen.</p>'
          : items.map(render).join(''));
        return section;
      }));
      for (const button of host.querySelectorAll('[data-act]'))
        button.onclick = () => ACTIONS[button.dataset.act](list.find(e => e.id === button.dataset.id));
    }

    // Only ids (slugs) go into attributes; names are escaped into text.
    function render(ev) {
      const id = Admin.escapeHtml(ev.id);
      const schedule = [ev.opensAt && `åpner ${when(ev.opensAt)}`, ev.closesAt && `stenger ${when(ev.closesAt)}`]
        .filter(Boolean).join(', ');
      return `
        <article class="event-row">
          <h3>${Admin.escapeHtml(ev.name)}${ev.isDefault ? ' <span class="event-label">fast</span>' : ''}</h3>
          <p class="muted">
            ${ev.counts.approved} på skjermen · ${ev.counts.pending} venter
            ${schedule ? ' · ' + Admin.escapeHtml(schedule) : ''}
          </p>
          ${ev.phase !== 'closed' && ev.joinUrl ? `
            <img class="event-qr" src="/api/join-qr.svg?event=${encodeURIComponent(ev.id)}" alt="QR-kode for ${Admin.escapeHtml(ev.name)}">
            <p><code>${Admin.escapeHtml(ev.joinUrl)}</code></p>` : ''}
          <div class="row">
            <a class="link-button" href="/show${ev.isDefault ? '' : '?event=' + encodeURIComponent(ev.id)}">Skjerm</a>
            <a class="link-button" href="/admin/images${ev.isDefault ? '' : '?event=' + encodeURIComponent(ev.id)}">Bilder</a>
            <a class="link-button" href="/api/events/${encodeURIComponent(ev.id)}/export.zip">Last ned alle (ZIP)</a>
            ${ev.isDefault ? '' : `<button class="small" data-act="schedule" data-id="${id}">Tidsplan</button>`}
            ${ev.isDefault ? '' : ev.phase === 'closed'
              ? `<button class="small" data-act="reopen" data-id="${id}">Åpne igjen</button>`
              : `<button class="small" data-act="close" data-id="${id}">Steng nå</button>`}
            <button class="small" data-act="rotate" data-id="${id}">Ny QR-kode</button>
            ${ev.isDefault ? '' : `<button class="small quiet-danger" data-act="remove" data-id="${id}">Slett…</button>`}
          </div>
        </article>`;
    }

    const ACTIONS = {
      async close(ev) {
        const sure = await Admin.confirm({
          title: `Stenge ${ev.name}?`,
          body: 'Boten tar ikke lenger imot bilder til arrangementet, og skjermen slutter å invitere. '
              + 'Bildene blir liggende til du sletter arrangementet.',
          confirmLabel: 'Steng',
        });
        if (sure && await Admin.api('POST', `/api/events/${encodeURIComponent(ev.id)}/close`)) Admin.toast(`${ev.name} er stengt.`);
        load();
      },
      async reopen(ev) {
        if (await Admin.api('POST', `/api/events/${encodeURIComponent(ev.id)}/reopen`)) Admin.toast(`${ev.name} er åpent igjen.`);
        load();
      },
      async rotate(ev) {
        const sure = await Admin.confirm({
          title: `Lage ny QR-kode for ${ev.name}?`,
          body: 'Den gamle koden og lenken slutter å virke med en gang. De som allerede er med, '
              + 'kan fortsatt sende bilder.',
          confirmLabel: 'Lag ny kode',
          danger: true,
        });
        if (sure && await Admin.api('POST', `/api/events/${encodeURIComponent(ev.id)}/rotate-code`)) Admin.toast('Ny kode er laget.');
        load();
      },
      schedule: editSchedule,
      remove: confirmDelete,
    };

    /** A dialog of its own: Admin.dialog has no input fields. */
    function form(title, fields, confirmLabel, danger = false) {
      return new Promise(resolve => {
        const dialog = document.createElement('dialog');
        dialog.className = 'admin-dialog';
        dialog.innerHTML = `<h2></h2>${fields}<div class="dialog-actions">
          <button type="button" value="cancel">Avbryt</button>
          <button type="button" value="ok" class="${danger ? 'danger' : 'primary'}"></button></div>`;
        dialog.querySelector('h2').textContent = title;
        dialog.querySelector('[value="ok"]').textContent = confirmLabel;
        dialog.querySelector('[value="cancel"]').onclick = () => { dialog.close(); resolve(null); };
        dialog.querySelector('[value="ok"]').onclick = () => { dialog.close(); resolve(dialog); };
        dialog.addEventListener('cancel', () => resolve(null));
        dialog.addEventListener('close', () => setTimeout(() => dialog.remove(), 0));
        document.body.appendChild(dialog);
        dialog.showModal();
      });
    }

    async function editSchedule(ev) {
      const dialog = await form(`Tidsplan for ${ev.name}`, `
        <label for="opens">Åpner</label><input id="opens" type="datetime-local" value="${toLocalInput(ev.opensAt)}">
        <label for="closes">Stenger</label><input id="closes" type="datetime-local" value="${toLocalInput(ev.closesAt)}">
        <p class="muted">La et felt stå tomt for ingen grense.</p>`, 'Lagre');
      if (!dialog) return;
      if (await Admin.api('PUT', `/api/events/${encodeURIComponent(ev.id)}/schedule`, {
        opensAt: toIso(dialog.querySelector('#opens').value),
        closesAt: toIso(dialog.querySelector('#closes').value),
      })) Admin.toast('Tidsplanen er lagret.');
      load();
    }

    async function confirmDelete(ev) {
      const dialog = await form(`Slette ${ev.name}?`, `
        <p>Alle ${ev.counts.total} bildene i arrangementet slettes fra lagringen. Last dem ned først
           hvis noen vil ha dem. Dette kan ikke angres.</p>
        <label for="confirmName">Skriv navnet på arrangementet for å bekrefte</label>
        <input id="confirmName" type="text" autocomplete="off">`, 'Slett arrangementet', true);
      if (!dialog) return;
      if (dialog.querySelector('#confirmName').value.trim() !== ev.name) {
        Admin.toast('Navnet stemte ikke. Ingenting ble slettet.', 'error');
        return;
      }
      const response = await Admin.api('DELETE', `/api/events/${encodeURIComponent(ev.id)}`);
      if (response) {
        const { deleted } = await response.json();
        Admin.toast(`${ev.name} er slettet, med ${deleted} bilder.`);
      }
      load();
    }

    Admin.onManifest(() => load());
    load();
  </script>
</body>
</html>
```

`admin.css`:

```css
.event-row { border-top: 1px solid var(--border, #ddd); padding-top: 12px; margin-top: 12px; }
.event-row:first-of-type { border-top: 0; margin-top: 0; }
.event-row h3 { margin: 0 0 4px; }
.event-qr { width: 120px; height: 120px; display: block; margin: 8px 0; }
```

- [ ] **Step 4: Scope the other pages**

- **`images.html`**: `load()` becomes

```js
    async function load() {
      const ev = await Admin.currentEvent();
      if (!ev) return;
      const response = await fetch(`/api/images?event=${encodeURIComponent(ev.id)}`, { cache: 'no-store' });
      if (!response.ok) return;
      all = await response.json();
      document.querySelector('.page-head h1').textContent = `Bilder – ${ev.name}`;
      render();
    }
```

  `removeAll` deletes `` `/api/images?event=${encodeURIComponent((await Admin.currentEvent()).id)}` ``, and its confirm body names the event: `` `Alle bilder i ${ev.name} fjernes fra skjermen og fra lagringen …` ``.
- **`settings.html`**: `fetch(Admin.withEvent('/api/settings'))`. Both saves use `Admin.api('PATCH', Admin.withEvent('/api/settings'), …)`. Rename the "Arrangement" card's heading to "Navn og visning".
- **`upload.html`**: `fetch(Admin.withEvent('/api/images'), { method: 'POST', body: form })`. Under the page head, add `<p class="muted" id="target"></p>` and set it with `Admin.currentEvent().then(ev => { if (ev) document.getElementById('target').textContent = \`Bildene går til ${ev.name}.\`; });` (use `textContent`, since it is text).
- **`queue.html`**: keep one queue for every open event:

```js
    let events = [];
    let filter = '';   // '' = every open event

    async function loadPending() {
      events = await Admin.events(true);
      const open = new Set(events.filter(e => e.phase === 'open').map(e => e.id));
      const response = await fetch('/api/images?status=pending', { cache: 'no-store' });
      if (!response.ok) return;
      // The API lists newest first.
      pending = (await response.json())
        .filter(image => open.has(image.eventId) && (!filter || image.eventId === filter))
        .reverse();
      renderFilter(open);
      render();
    }

    const eventName = id => events.find(e => e.id === id)?.name ?? id;

    function renderFilter(open) {
      const host = document.getElementById('event-filter');
      host.hidden = open.size < 2;
      host.innerHTML = `<option value="">Alle åpne arrangementer</option>` +
        events.filter(e => open.has(e.id))
          .map(e => `<option value="${Admin.escapeHtml(e.id)}" ${e.id === filter ? 'selected' : ''}>${Admin.escapeHtml(e.name)}</option>`)
          .join('');
      host.onchange = () => { filter = host.value; currentId = null; loadPending(); };
    }
```

  Add `<select id="event-filter" aria-label="Arrangement" hidden></select>` to the page head. In `render()`, directly after the sender span, add `<span class="event-label">${Admin.escapeHtml(eventName(image.eventId))}</span>`.

- [ ] **Step 5: Check it in the browser**

`preview_start` `{ "name": "local" }`, log in with `dev`, then:
1. On `/admin/events`, create "Bryllup" (the id suggestion should read `bryllup`).
   Expected: a card under "Åpne" with a QR image, and the nav gains the event picker.
2. Pick "Bryllup" in the picker on `/admin/images`.
   Expected: the URL gets `?event=bryllup`, the heading reads "Bilder – Bryllup", and nav links (except Telegram and Arrangementer) carry `?event=bryllup`.
3. On `/upload?event=bryllup`, upload a test photo (`tests/EventPhotoBot.Tests/TestAssets/landscape.jpg`).
   Expected: it shows on `/admin/images?event=bryllup` and not on `/admin/images`.
4. On `/admin/events`, choose "Steng nå" for Bryllup.
   Expected: it moves to "Avsluttede", the QR disappears, and the picker shows "Bryllup (avsluttet)".
5. Choose "Slett…", type a wrong name.
   Expected: the "Navnet stemte ikke" toast and nothing is deleted. Typing the right name deletes it.
6. `read_console_messages` with `onlyErrors: true`.
   Expected: none. `resize_window` `{ "preset": "mobile" }` on `/admin/events`: the rows wrap and nothing scrolls sideways. Then reset with `{ "preset": "desktop" }`.

Take a screenshot of `/admin/events` for the task report.

- [ ] **Step 6: Run the suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add -A src tests
git commit -m "feat(admin): an events page, an event picker, and every page scoped to the chosen event

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: The screen follows `?event=`

**Files:**
- Modify: `src/EventPhotoBot/wwwroot/show.js`

- [ ] **Step 1: Implement**

Near the top of the IIFE in `show.js`, with the other constants:

```js
  // Which event this screen shows. Absent is the default event, so the church
  // screen's bookmark from before events keeps working unchanged.
  const eventId = new URLSearchParams(location.search).get('event');
  const scoped = path => eventId ? `${path}?event=${encodeURIComponent(eventId)}` : path;
```

- In `poll()`: `fetch(scoped('/api/manifest'), …)`.
- In `applyJoin()`: both `'/api/join-qr.svg'` assignments become `scoped('/api/join-qr.svg')`.
- In `applyJoin()`, the QR `src` is set once while `joinReady`, but a screen whose event closes gets `joinUrl` null. Add, before `joinEl.hidden = …`: `if (!joinUrl) joinReady = false;`. That way a reopened event re-arms the QR on the next poll.

- [ ] **Step 2: Check it in the browser**

With `local` running and an open event `bryllup` holding one approved photo (create it as in Task 11):
1. `navigate` to `/show`: the default event's photos and its QR.
2. `/show?event=bryllup`: only the wedding photo, and the corner name "Bryllup".
3. Close Bryllup on `/admin/events` in another tab, and within a few seconds `/show?event=bryllup` hides the invite (`read_page` shows no QR element visible).
4. `read_network_requests` with `urlPattern: "manifest"`: the requests carry `?event=bryllup`.

- [ ] **Step 3: Commit**

```bash
git add src/EventPhotoBot/wwwroot/show.js
git commit -m "feat(show): /show?event= shows that event's photos and QR

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: The `/dev` simulator knows about events and buttons

**Files:**
- Modify: `src/EventPhotoBot/Web/DevEndpoints.cs`, `src/EventPhotoBot/DevTools/dev.html`

**Interfaces:**
- Consumes: `BotReply.MessageId/Buttons/Edited` (Task 5), `TgCallbackQuery` (Task 5).
- Produces: `DevGuest.EventId` (optional), `POST /dev/tap`

- [ ] **Step 1: Implement**

`DevEndpoints.cs`:
- `DevGuest` gains a trailing `string? EventId = null`.
- `/dev/join` uses `(store.Snapshot.Find(guest.EventId) ?? store.Snapshot.Default()).JoinCode`.
- Add

```csharp
        // A guest tapping one of the bot's inline buttons, the way Telegram reports it.
        app.MapPost("/dev/tap", async (DevTap tap, UpdateHandler handler) =>
        {
            await handler.HandleAsync(new TgUpdate
            {
                UpdateId = Interlocked.Increment(ref _nextUpdateId),
                CallbackQuery = new TgCallbackQuery
                {
                    Id = Guid.NewGuid().ToString("N"),
                    From = new TgUser { Id = tap.Id, FirstName = string.IsNullOrWhiteSpace(tap.Name) ? null : tap.Name },
                    Message = new TgMessage { MessageId = tap.MessageId, Chat = new TgChat { Id = tap.Id, Type = "private" } },
                    Data = tap.Data,
                },
            });
            return Results.Ok();
        });
```

  with `public sealed record DevTap(long Id, string? Name, long MessageId, string Data);`.

`dev.html`:
- In the "Gjest" card, add an event picker:

```html
      <div class="field">
        <label for="guestEvent">Skann QR-koden til</label>
        <select id="guestEvent"></select>
      </div>
```

  filled on load from `/api/events` (open events only). `guest()` then includes `eventId: document.getElementById('guestEvent').value`.
- In `loadReplies`, render the buttons under a reply and mark edits:

```js
        const text = document.createElement('p');
        text.textContent = reply.reaction
          ? (reply.text ? `Reagerte med ${reply.text} på bildet` : 'Fjernet reaksjonen på bildet')
          : (reply.edited ? `(endret) ${reply.text}` : reply.text);
        item.append(meta, text);
        if (reply.buttons?.length) {
          const row = document.createElement('div');
          row.className = 'row';
          for (const button of reply.buttons) {
            const tap = document.createElement('button');
            tap.className = 'small';
            tap.textContent = button.text;
            tap.onclick = async () => {
              const { id, name } = guest();
              await Admin.api('POST', '/dev/tap', { id, name, messageId: reply.messageId, data: button.callbackData });
              loadReplies();
            };
            row.appendChild(tap);
          }
          item.appendChild(row);
        }
```

  (`OfflineTelegramClient.Replies` serializes `InlineButton` as `{ text, callbackData }` through ASP.NET's camelCase defaults.)

- [ ] **Step 2: Check it in the browser**

With `local` running and an open event `bryllup`:
1. On `/dev`, as a new guest, join "Daglig", then join "Bryllup".
   Expected: the second reply reads "Du sender nå bilder til Bryllup." with a "Daglig" button.
2. Tap "Daglig".
   Expected: an "(endret) Du sender nå bilder til Daglig." entry with a "Bryllup" button.
3. Send a photo.
   Expected: "Mottatt til Daglig — …".
4. Approve it on `/admin/queue` after posting it in a simulated group routed to Daglig.
   Expected: "Reagerte med 🔥 på bildet".

- [ ] **Step 3: Run the suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add -A src
git commit -m "feat(dev): simulate joining any event and tapping the bot's buttons

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Documentation and the spec amendment

**Files:**
- Modify: `README.md`, `docs/RUNBOOK.md`, `docs/superpowers/specs/2026-09-23-multiple-events-design.md`, `docs/BEYOND-ONE-EVENT.md`

- [ ] **Step 1: Amend the spec**

Append to the spec a section `## Changes made while planning`. It should restate the "Decisions made while planning" list from this plan's Global Constraints, one bullet each, covering:
- the lifecycle rule
- reactions on bulk deletes
- the split event endpoints
- the group routing body
- scheduled-event replies
- the scheduled QR
- non-empty names
- the 900 s timeout
- UTC export names

In the spec's API table:
- add `PUT /api/events/{id}/schedule`, `PUT /api/events/{id}/retention` and `POST /api/events/{id}/retention/run`
- note that `PATCH /api/events/{id}` takes the name only

- [ ] **Step 2: README and runbook**

`README.md`:
- In the feature overview, describe events: the daily default, special events with their own QR and `/show?event=`, groups routed in `/admin/telegram`, and retention.
- In the local-run section, note that `/dev` can join any event and tap the bot's buttons.

`docs/RUNBOOK.md`:
- **Secrets:** `JOIN_CODE` is now optional and only seeds the default event on the first start after the upgrade. `RETENTION_SECRET` is new (`openssl rand -hex 32`).
- **Upgrade:** the first start rewrites `state.json` into the events shape, and `state/state-prev.json` holds the pre-upgrade file for as long as nothing else is written. To roll back, redeploy the previous image and copy `state-prev.json` over `state.json` before anything else writes.
- **Retention:** the Cloud Scheduler job `<name>-retention` runs at 03:15 Europe/Oslo. Its region is `-SchedulerRegion`. "Rydd nå" in Innstillinger runs the same sweep for one event.
- **No bucket lifecycle rule any more:** photos stay until deleted, by an organiser or by retention.
- **Export:** a large event's ZIP can take minutes. Download it from the run.app URL, because Firebase Hosting cuts requests at 60 s.
- **Group walkthrough:** replace "post the join code in the group" with "route the group to an event in Telegram → Grupper".
- **Acceptance checks:** add
  - a wedding guest's photo appears on `/show?event=<id>` and not on `/show`
  - after closing the wedding, a daily member's next photo lands on Daglig with "Mottatt til Daglig"
  - approving a group photo turns 👀 into 🔥

`docs/BEYOND-ONE-EVENT.md`: under "3. Events as a first-class thing", add a first line: "Done in part (2026-09-23): events, per-event settings, per-event auto-approve, rotatable join codes, retention and export. See docs/superpowers/specs/2026-09-23-multiple-events-design.md. Accounts, roles and a database are still ahead."

- [ ] **Step 3: Commit**

```bash
git add README.md docs
git commit -m "docs: multiple events in the README, runbook and spec

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-review notes

- **Spec coverage:** data model (1), migration (1), open/closed/scheduled (2), join codes (1, 2), groups (6), private chat (4, 5), dedup (4), bans (8), texts (4, 6), plumbing (5, 13), admin event context (11), `/admin/events` (11), `/admin/telegram` (6, 8), API table (2, 3, 6, 8, 9, 10), screens (3, 12), retention (9), error handling (2, 5, 7, 9), testing (every task), out of scope (Global Constraints), "to verify" items (Task 9's gcloud flags; the reaction emoji set is unchanged from today's 👀/🔥, which the app already sends).
- **Types used across tasks:**
  - `Routing.ResolvePrivateTarget`, `Routing.SwitchButtons`, `Routing.CallbackPrefix`
  - `EventRules.Default`/`Find`/`EventOf`/`MembershipIn`/`IsOpen`/`PhaseAt`/`UniqueJoinCode`/`GenerateJoinCode`
  - `EventScope.Resolve`/`UnknownEvent`
  - `EventEndpoints.CleanName`/`BadRequest`
  - `ImageObjects.DeleteAsync`, `Reactions.SyncAsync`/`ClearAsync`, `RetentionSweep.Select`/`RunAsync`
  - `TestState.New`/`AddEvent`, `StateMigration.DefaultEventId`
