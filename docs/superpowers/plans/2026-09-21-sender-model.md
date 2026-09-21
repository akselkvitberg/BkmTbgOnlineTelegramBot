# Sender Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the whitelist-as-gate with a three-status sender roster, admit
event guests through a QR-delivered join code, and make banning a sender revoke
the photos they already sent.

**Architecture:** One `List<Sender>` in `Settings` carries a `SenderStatus` per
person (`Known`, `AutoApprove`, `Banned`); absence from the list means the
person has not redeemed the join code and nothing is stored for them.
`UpdateHandler` makes one roster lookup before any download. The join code is a
deploy-time Secret Manager value rendered as a Telegram deep-link QR on the
slideshow.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit, QRCoder, Terraform,
Google Cloud Run + Secret Manager.

**Spec:** [`docs/superpowers/specs/2026-09-21-sender-model-design.md`](../specs/2026-09-21-sender-model-design.md)

## Global Constraints

- Branch: `feat/sender-model`. Do not commit to `master`.
- `SixLabors.ImageSharp` stays pinned to **3.1.12**. Do not bump it — 4.x gates
  Release builds on a licence-enrollment check and the container build is
  Release.
- Never log a secret value. `AppConfig.LogLoaded` logs key names only; keep it
  that way for `JOIN_CODE`.
- Secret *values* never pass through Terraform variables or GitHub secrets.
  Terraform creates the secret resource; a human adds the version with
  `gcloud secrets versions add`.
- Join code charset is exactly `^[A-Za-z0-9_-]{1,64}$` — Telegram's deep-link
  payload charset.
- Comparisons of attacker-reachable secrets are constant-time via hash-then-
  `CryptographicOperations.FixedTimeEquals`, matching `SecretTokenMatches` in
  `Program.cs`.
- Run the full suite with `dotnet test EventPhotoBot.slnx`. A single test with
  `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~<name>"`.
- Tests are named as sentences in `Pascal_snake_case`, matching the existing
  suite.

---

### Task 1: JOIN_CODE configuration

**Files:**
- Modify: `src/EventPhotoBot/AppConfig.cs`
- Test: `tests/EventPhotoBot.Tests/AppConfigTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AppConfig.JoinCode` (`string`, trimmed, matches
  `^[A-Za-z0-9_-]{1,64}$`). Tasks 4 and 6 read it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EventPhotoBot.Tests/AppConfigTests.cs`. Also add
`("JOIN_CODE", "party2026")` to the `Complete()` array so the existing tests
keep passing.

```csharp
[Fact]
public void Load_binds_the_join_code()
{
    var config = AppConfig.Load(Config(Complete()));

    Assert.Equal("party2026", config.JoinCode);
}

[Fact]
public void Load_throws_when_the_join_code_is_missing()
{
    var partial = Complete().Where(p => p.Item1 is not "JOIN_CODE").ToArray();

    var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(partial)));

    Assert.Contains("JOIN_CODE", error.Message);
}

[Theory]
[InlineData("party 2026")]      // space
[InlineData("party!")]          // punctuation outside the payload charset
[InlineData("rødt")]            // non-ASCII
public void Load_rejects_a_join_code_outside_the_deep_link_charset(string code)
{
    var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
        .Append(("JOIN_CODE", code)).ToArray();

    var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(pairs)));

    Assert.Contains("JOIN_CODE", error.Message);
}

[Fact]
public void Load_rejects_a_join_code_over_sixty_four_characters()
{
    var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
        .Append(("JOIN_CODE", new string('a', 65))).ToArray();

    Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(pairs)));
}

[Fact]
public void A_join_code_with_a_trailing_newline_is_trimmed_and_accepted()
{
    var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
        .Append(("JOIN_CODE", "party2026\n")).ToArray();

    Assert.Equal("party2026", AppConfig.Load(Config(pairs)).JoinCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~AppConfigTests"`
Expected: FAIL — `AppConfig` has no `JoinCode` member (compile error).

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/AppConfig.cs`, add `using System.Text.RegularExpressions;`
at the top, then:

```csharp
public required string JoinCode { get; init; }
```

Add `"JOIN_CODE"` to the end of the `SecretKeys` array so it is both required
and reported by `LogLoaded` (which logs the key name, never the value).

Above `Load`, add:

```csharp
// Telegram's deep-link payload charset. A code outside it produces a
// https://t.me/<bot>?start=<code> link whose payload Telegram silently drops,
// so every scan lands in the chat with no code attached and the person is
// declined with no clue why. Failing the deploy is the cheap end of that.
private static readonly Regex JoinCodePattern =
    new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
```

In `Load`, after the missing-key check and before constructing the config:

```csharp
var joinCode = config["JOIN_CODE"]!.Trim();
if (!JoinCodePattern.IsMatch(joinCode))
{
    throw new InvalidOperationException(
        "JOIN_CODE must be 1-64 characters from A-Z, a-z, 0-9, underscore or hyphen " +
        "(Telegram's deep-link payload charset). Fix the secret version and redeploy.");
}
```

Then add `JoinCode = joinCode,` to the returned `AppConfig` initialiser.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS. Every test in the suite — `AppFactory` needs the new setting
too, so if `AdminApiTests` and friends fail with "Missing required
configuration: JOIN_CODE", add `builder.UseSetting("JOIN_CODE", JoinCode);` to
`tests/EventPhotoBot.Tests/AppFactory.cs` alongside the other `UseSetting`
calls, with `public const string JoinCode = "party2026";` next to the other
constants there.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/AppConfig.cs tests/EventPhotoBot.Tests/AppConfigTests.cs tests/EventPhotoBot.Tests/AppFactory.cs
git commit -m "feat: require a JOIN_CODE secret, validated against Telegram's deep-link charset"
```

---

### Task 2: Bot username lookup

**Files:**
- Modify: `src/EventPhotoBot/Telegram/ITelegramClient.cs`
- Modify: `src/EventPhotoBot/Telegram/TelegramClient.cs`
- Create: `src/EventPhotoBot/Telegram/BotIdentity.cs`
- Modify: `src/EventPhotoBot/Program.cs`
- Modify: `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs`
- Test: `tests/EventPhotoBot.Tests/BotIdentityTests.cs`

**Interfaces:**
- Consumes: `AppConfig.JoinCode` from Task 1.
- Produces: `ITelegramClient.GetMeAsync(CancellationToken) -> Task<string?>`
  returning the bot username without `@`, or null on failure.
  `BotIdentity.JoinUrl` (`string?`) — the deep link, or null when the username
  is unknown. Tasks 6 and 9 read `JoinUrl`.

- [ ] **Step 1: Write the failing test**

Create `tests/EventPhotoBot.Tests/BotIdentityTests.cs`:

```csharp
using EventPhotoBot;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class BotIdentityTests
{
    private static AppConfig Config() => new()
    {
        BucketName = "bucket", EventName = "Party", BotToken = "token",
        WebhookSecret = "secret", WebhookPath = "abc123", AdminPassword = "hunter2",
        CookieSigningKey = "0123456789abcdef0123456789abcdef", JoinCode = "party2026",
    };

    [Fact]
    public async Task Resolve_builds_the_deep_link_from_the_username_and_join_code()
    {
        var telegram = new FakeTelegramClient { Username = "eventphotobot" };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Equal("https://t.me/eventphotobot?start=party2026", identity.JoinUrl);
        Assert.Equal("eventphotobot", identity.Username);
    }

    [Fact]
    public async Task An_unavailable_username_leaves_the_join_link_null_without_throwing()
    {
        var telegram = new FakeTelegramClient { Username = null };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Null(identity.JoinUrl);
    }

    [Fact]
    public async Task A_throwing_getMe_leaves_the_join_link_null_without_throwing()
    {
        var telegram = new FakeTelegramClient { GetMeThrows = true };
        var identity = new BotIdentity(Config());

        await identity.ResolveAsync(telegram, NullLogger<BotIdentity>.Instance);

        Assert.Null(identity.JoinUrl);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~BotIdentityTests"`
Expected: FAIL — `BotIdentity` does not exist (compile error).

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/Telegram/ITelegramClient.cs`, add to the interface:

```csharp
    /// <summary>The bot's own @username, or null if Telegram would not say.</summary>
    Task<string?> GetMeAsync(CancellationToken ct = default);
```

In `src/EventPhotoBot/Telegram/TelegramClient.cs`:

```csharp
    public async Task<string?> GetMeAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/getMe", ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("getMe failed: {StatusCode}", (int)response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.GetProperty("result")
            .TryGetProperty("username", out var username) ? username.GetString() : null;
    }
```

Create `src/EventPhotoBot/Telegram/BotIdentity.cs`:

```csharp
namespace EventPhotoBot.Telegram;

/// <summary>
/// The bot's own username, learned once at startup, and the join deep link built
/// from it. Resolved rather than configured because the username is Telegram's to
/// know, not the operator's to retype correctly.
///
/// A failure here is deliberately not fatal. A missing QR costs one affordance;
/// refusing to start costs the event its screen. This is the opposite of the
/// missing-secret case, where the service genuinely cannot work.
/// </summary>
public sealed class BotIdentity(AppConfig config)
{
    public string? Username { get; private set; }

    public string? JoinUrl =>
        Username is null ? null : $"https://t.me/{Username}?start={config.JoinCode}";

    public async Task ResolveAsync(ITelegramClient telegram, ILogger<BotIdentity> logger)
    {
        try
        {
            Username = await telegram.GetMeAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resolve the bot username; the join QR will be omitted.");
            return;
        }

        if (Username is null)
            logger.LogWarning("Telegram returned no username; the join QR will be omitted.");
        else
            logger.LogInformation("Bot username resolved.");
    }
}
```

In `src/EventPhotoBot/Program.cs`, register it beside the other singletons
(after `builder.Services.AddSingleton<UpdateHandler>();`):

```csharp
builder.Services.AddSingleton<BotIdentity>();
```

And after the existing `await app.Services.GetRequiredService<StateStore>().LoadAsync();`:

```csharp
await app.Services.GetRequiredService<BotIdentity>().ResolveAsync(
    app.Services.GetRequiredService<ITelegramClient>(),
    app.Services.GetRequiredService<ILogger<BotIdentity>>());
```

In `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs`, replace the
`// Stub for now, completed in Task 10.` comment (it is stale) and add:

```csharp
    public string? Username { get; set; } = "eventphotobot";
    public bool GetMeThrows { get; set; }

    public Task<string?> GetMeAsync(CancellationToken ct = default) =>
        GetMeThrows
            ? throw new HttpRequestException("getMe unavailable")
            : Task.FromResult(Username);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Telegram/ src/EventPhotoBot/Program.cs tests/EventPhotoBot.Tests/
git commit -m "feat: resolve the bot username at startup and build the join deep link"
```

---

### Task 3: Sender roster model

Adds the roster alongside the existing whitelist fields so the tree keeps
compiling. Task 5 removes the old fields once nothing reads them.

**Files:**
- Modify: `src/EventPhotoBot/State/Models.cs`
- Test: `tests/EventPhotoBot.Tests/StateModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `SenderStatus` enum (`Known`, `AutoApprove`, `Banned`);
  `Sender` class with `long Id`, `string Name`, `SenderStatus Status`,
  `DateTimeOffset FirstSeen`; `Settings.Senders` (`List<Sender>`).
  Tasks 4, 5, 7 and 8 use these.

- [ ] **Step 1: Write the failing test**

Add to `tests/EventPhotoBot.Tests/StateModelTests.cs`:

```csharp
[Fact]
public void A_sender_roster_round_trips_through_json()
{
    var state = new EventState();
    state.Settings.Senders.Add(new Sender
    {
        Id = 42,
        Name = "Guest",
        Status = SenderStatus.AutoApprove,
        FirstSeen = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
    });

    var json = JsonSerializer.SerializeToUtf8Bytes(state, StateJson.Options);
    var round = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

    var sender = Assert.Single(round.Settings.Senders);
    Assert.Equal(42, sender.Id);
    Assert.Equal(SenderStatus.AutoApprove, sender.Status);
}

[Fact]
public void A_fresh_state_has_an_empty_roster()
{
    Assert.Empty(new EventState().Settings.Senders);
}
```

If `StateModelTests.cs` lacks them, add `using System.Text.Json;` and
`using EventPhotoBot.State;`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~StateModelTests"`
Expected: FAIL — `Sender` and `SenderStatus` do not exist (compile error).

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/State/Models.cs`, add next to the other small types:

```csharp
/// <summary>
/// What the bot does with a person's photos. Absence from the roster is the
/// fourth case and needs no member: that person has not redeemed the join code,
/// nothing is stored for them, and their photos are declined.
/// </summary>
public enum SenderStatus
{
    /// <summary>Redeemed the join code. Photos go to the approval queue.</summary>
    Known,

    /// <summary>A pre-approved photographer. Photos go straight to the screen.</summary>
    AutoApprove,

    /// <summary>Blocked. Messages are dropped silently, nothing is downloaded.</summary>
    Banned,
}

public sealed class Sender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public SenderStatus Status { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}
```

In `Settings`, add:

```csharp
    public List<Sender> Senders { get; set; } = [];
```

Check how `StateJson.Options` serialises enums. If it has a
`JsonStringEnumConverter`, `SenderStatus` serialises as a string and nothing
more is needed. If it does not, leave it — integer enum values round-trip
correctly, and `ImageStatus` already relies on the same behaviour. Do not change
the converter set: that would rewrite how existing `ImageStatus` values
serialise.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/State/Models.cs tests/EventPhotoBot.Tests/StateModelTests.cs
git commit -m "feat: add the sender roster to state, alongside the whitelist"
```

---

### Task 4: UpdateHandler reads the roster

**Files:**
- Modify: `src/EventPhotoBot/Telegram/UpdateHandler.cs`
- Test: `tests/EventPhotoBot.Tests/UpdateHandlerTests.cs`

**Interfaces:**
- Consumes: `AppConfig.JoinCode` (Task 1); `Sender`, `SenderStatus`,
  `Settings.Senders` (Task 3).
- Produces: `UpdateHandler(StateStore, IObjectStore, ITelegramClient, AppConfig, ILogger<UpdateHandler>)`
  — note the added `AppConfig` parameter, which `Program.cs` supplies through DI
  automatically and the test harness must pass explicitly.

- [ ] **Step 1: Write the failing tests**

In `tests/EventPhotoBot.Tests/UpdateHandlerTests.cs`:

Add the config to the harness. Add a field to `Harness`:

```csharp
        public const string JoinCode = "party2026";

        private static AppConfig Config() => new()
        {
            BucketName = "bucket", EventName = "Party", BotToken = "token",
            WebhookSecret = "secret", WebhookPath = "abc123", AdminPassword = "hunter2",
            CookieSigningKey = "0123456789abcdef0123456789abcdef", JoinCode = JoinCode,
        };
```

and change the `UpdateHandler` construction inside `CreateAsync` to:

```csharp
            harness.Handler = new UpdateHandler(
                harness.Store, harness.Objects, harness.Telegram, Config(),
                NullLogger<UpdateHandler>.Instance);
```

Add a helper next to `PhotoFrom`:

```csharp
    private static TgUpdate TextFrom(long userId, string text) => new()
    {
        UpdateId = 1,
        Message = new TgMessage
        {
            From = new TgUser { Id = userId, FirstName = "Guest" },
            Chat = new TgChat { Id = userId },
            Text = text,
        },
    };

    private static Sender Roster(long id, SenderStatus status) =>
        new() { Id = id, Name = "Guest", Status = status, FirstSeen = DateTimeOffset.UtcNow };
```

**Delete** these now-meaningless tests, which assert the gate behaviour this
task replaces:

- `A_whitelisted_sender_gets_their_photo_queued`
- `A_trusted_sender_skips_the_queue_when_auto_approve_is_on`
- `A_trusted_sender_still_queues_when_auto_approve_is_off`
- `An_unlisted_sender_is_declined_and_nothing_is_stored`
- `Pairing_mode_replies_with_the_id_and_records_the_sender_without_storing_photos`
- `Pairing_mode_records_a_sender_only_once`
- `Start_tells_an_unlisted_sender_they_are_not_on_the_list`
- `Start_tells_a_listed_sender_what_happens_to_their_photos`

In every **remaining** test, replace
`s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" })` with
`s.Senders.Add(Roster(Guest, SenderStatus.Known))`.

Then add:

```csharp
    [Fact]
    public async Task A_known_sender_gets_their_photo_queued()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Pending, image.Status);
        Assert.Equal(Guest, image.SenderId);
    }

    [Fact]
    public async Task An_auto_approve_sender_skips_the_queue()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.AutoApprove)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task A_banned_sender_gets_no_reply_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, SenderStatus.Banned)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Objects.Paths);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task An_unredeemed_sender_is_told_to_scan_the_qr_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Objects.Paths);
        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task The_join_code_admits_a_new_sender_as_known()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Settings.Senders);
        Assert.Equal(Stranger, sender.Id);
        Assert.Equal(SenderStatus.Known, sender.Status);
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_wrong_join_code_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start wrongcode"));

        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task A_bare_start_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start"));

        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
    }

    [Fact]
    public async Task Redeeming_the_code_again_does_not_demote_an_auto_approve_sender()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.AutoApprove)));

        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.AutoApprove, Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
    }

    [Fact]
    public async Task A_banned_sender_cannot_readmit_themselves_with_the_code()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, SenderStatus.Banned)));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.Banned, Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Redemption_is_not_blocked_by_the_decline_cooldown()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));          // declined, starts the cooldown
        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.Known, Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
        Assert.Equal(2, harness.Telegram.Sent.Count);
    }

    [Fact]
    public async Task Start_tells_a_known_sender_what_happens_to_their_photos()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

        await harness.Handler.HandleAsync(TextFrom(Guest, "/start"));

        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("deleted after the event", text);
    }
```

Rename the two rate-limit tests to match the new vocabulary:
`A_second_decline_to_the_same_unredeemed_sender_is_rate_limited` and
`Rate_limiting_an_unredeemed_sender_does_not_affect_replies_to_a_different_sender`,
updating their bodies to use no roster entry rather than an absent whitelist.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~UpdateHandlerTests"`
Expected: FAIL — `UpdateHandler` has no five-argument constructor (compile error).

- [ ] **Step 3: Implement**

Rewrite the top of `src/EventPhotoBot/Telegram/UpdateHandler.cs`. Change the
primary constructor to:

```csharp
public sealed class UpdateHandler(
    StateStore store,
    IObjectStore objects,
    ITelegramClient telegram,
    AppConfig config,
    ILogger<UpdateHandler> logger)
```

Add, next to the other private fields:

```csharp
    private const string JoinPrompt =
        "You are not on the list for this event yet. Scan the QR code on the screen — " +
        "it will send me the code and you can start sending photos straight away.";
```

Replace `HandleAsync` with:

```csharp
    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        var message = update.Message;
        if (message?.From is not { } sender || message.Chat is not { } chat) return;

        var entry = store.Snapshot.Settings.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, and before anything that costs a download, a reply or a
        // write. A banned sender gets no signal at all — a reply would both confirm
        // the ban landed and make the bot a reply relay for whoever earned it.
        if (entry is { Status: SenderStatus.Banned }) return;

        if (entry is null)
        {
            await HandleUnredeemedAsync(message, sender, chat, ct);
            return;
        }

        if (message.Text is { } text && text.StartsWith("/start", StringComparison.Ordinal))
        {
            await telegram.SendMessageAsync(chat.Id,
                "Send me photos and they will go up on the screen at the event. " +
                "An organiser approves them first. Everything is deleted after the event.", ct);
            return;
        }

        var candidate = Extract(message);
        if (candidate is null)
        {
            await telegram.SendMessageAsync(chat.Id,
                "Photos only, please — I cannot show videos, stickers or animations.", ct);
            return;
        }

        if (candidate.DeclineReason is { } reason)
        {
            await telegram.SendMessageAsync(chat.Id, reason, ct);
            return;
        }

        await IngestAsync(message, sender, chat, entry, candidate, ct);
    }
```

Replace `HandleUnlistedAsync` with:

```csharp
    /// <summary>
    /// Nobody in the roster. Either they are redeeming the join code, or they found
    /// the bot some other way and need pointing at the QR. Nothing is stored in the
    /// second case: recording every stranger who pokes the bot would let anyone who
    /// finds the handle grow the roster with writes nobody authorised.
    /// </summary>
    private async Task HandleUnredeemedAsync(
        TgMessage message, TgUser sender, TgChat chat, CancellationToken ct)
    {
        if (message.Text is { } text && TryReadJoinCode(text, out var supplied)
            && JoinCodeMatches(config.JoinCode, supplied))
        {
            await store.MutateAsync(state =>
            {
                // Re-checked under the store's lock: two /start messages racing must
                // not produce two rows, and a row added by an admin in the meantime
                // must not be overwritten with a weaker status.
                if (state.Settings.Senders.Any(s => s.Id == sender.Id)) return;
                state.Settings.Senders.Add(new Sender
                {
                    Id = sender.Id,
                    Name = sender.DisplayName,
                    Status = SenderStatus.Known,
                    FirstSeen = DateTimeOffset.UtcNow,
                });
            }, ct);

            // Deliberately outside the throttle: being rate-limited out of joining is
            // the worst possible moment to go quiet on somebody.
            await telegram.SendMessageAsync(chat.Id,
                "You are in. Send me photos and they will go up on the screen once an " +
                "organiser approves them. Everything is deleted after the event.", ct);
            return;
        }

        if (ShouldReplyToUnlisted(sender.Id))
            await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
    }

    /// <summary>"/start CODE" — the payload Telegram appends from a deep link.</summary>
    private static bool TryReadJoinCode(string text, out string code)
    {
        code = "";
        if (!text.StartsWith("/start", StringComparison.Ordinal)) return false;

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        code = parts[1];
        return true;
    }

    /// <summary>
    /// Constant-time, hashing both sides first. Same reasoning as the webhook
    /// secret check in Program.cs: this is reachable by anyone who finds the bot,
    /// and FixedTimeEquals on its own leaks length through its argument check.
    /// </summary>
    private static bool JoinCodeMatches(string expected, string supplied)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
```

Add `using System.Security.Cryptography;` and `using System.Text;` at the top of
the file.

In `IngestAsync`, change the signature's `WhitelistEntry entry` to
`Sender entry`, and change the approval line inside `MutateAsync` from
`approved = entry.Trusted && state.Settings.AutoApproveTrusted;` to:

```csharp
                approved = entry.Status == SenderStatus.AutoApprove;
```

Update the XML comment on `_lastUnlistedReplyAt` to say "a sender who has not
redeemed the join code" rather than "a sender who is not on the whitelist", and
drop its mention of pairing mode.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS, all tests. `Program.cs` needs no change — `AppConfig` is already
a registered singleton, so DI supplies the new constructor parameter.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Telegram/UpdateHandler.cs tests/EventPhotoBot.Tests/UpdateHandlerTests.cs
git commit -m "feat: admit senders by join code, auto-approve the roster, drop banned senders silently"
```

---

### Task 5: Senders API and the ban cascade

**Files:**
- Modify: `src/EventPhotoBot/Web/ApiEndpoints.cs`
- Modify: `src/EventPhotoBot/State/Models.cs`
- Test: `tests/EventPhotoBot.Tests/SenderApiTests.cs` (create)
- Test: `tests/EventPhotoBot.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: `Sender`, `SenderStatus`, `Settings.Senders` (Task 3).
- Produces: `POST /api/senders/{id}/status` accepting
  `{ "status": "known" | "autoApprove" | "banned" }`; `GET /api/settings`
  returning `senders`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EventPhotoBot.Tests/SenderApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class SenderApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public SenderApiTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(long senderId, ImageStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status, SenderId = senderId,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Setting_a_status_creates_a_row_for_an_unknown_id()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5001/status", new { status = "autoApprove" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sender = Assert.Single(_factory.Store.Snapshot.Settings.Senders, s => s.Id == 5001);
        Assert.Equal(SenderStatus.AutoApprove, sender.Status);
    }

    [Fact]
    public async Task Banning_a_sender_rejects_every_image_they_sent()
    {
        var client = _factory.CreateAuthenticatedClient();
        var pending = await SeedImageAsync(5002, ImageStatus.Pending);
        var approved = await SeedImageAsync(5002, ImageStatus.Approved);

        await client.PostAsJsonAsync("/api/senders/5002/status", new { status = "banned" });

        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[pending].Status);
        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[approved].Status);
    }

    [Fact]
    public async Task Banning_the_holder_of_takeover_clears_it_in_the_same_write()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(5003, ImageStatus.Approved);
        await _factory.Store.MutateAsync(s => s.Settings.TakeoverImageId = id);

        await client.PostAsJsonAsync("/api/senders/5003/status", new { status = "banned" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Banning_a_sender_with_no_images_succeeds()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5004/status", new { status = "banned" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_status_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5005/status", new { status = "vip" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setting_a_status_needs_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/senders/5006/status", new { status = "banned" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_settings_patch_cannot_write_the_roster()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/senders/5007/status", new { status = "known" });

        await client.PatchAsJsonAsync("/api/settings", new { senders = Array.Empty<object>() });

        Assert.Contains(_factory.Store.Snapshot.Settings.Senders, s => s.Id == 5007);
    }
}
```

In `tests/EventPhotoBot.Tests/AdminApiTests.cs`, delete any test asserting on
`whitelist`, `pairingMode`, `autoApproveTrusted` or `seenSenders` in the
settings payload or patch. Search for those four names and remove the
assertions; if a whole test exists only for them, delete it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~SenderApiTests"`
Expected: FAIL — 404 on `/api/senders/...`.

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/Web/ApiEndpoints.cs`, add the request record beside the
others at the top:

```csharp
public sealed record SenderStatusRequest(string Status);
```

Change `SettingsPatch` to drop the four removed members:

```csharp
public sealed record SettingsPatch(
    int? SlideSeconds,
    int? TransitionMs,
    string? Order,
    bool? NewestFirstBoost,
    int? RecurringEvery);
```

In `MapApi`, replace the whitelist-related properties in the `/api/settings`
response with `s.Senders`, so the anonymous object ends:

```csharp
                s.TakeoverImageId,
                s.TakeoverUntil,
                s.Senders,
```

(removing `s.AutoApproveTrusted`, `s.PairingMode`, `s.Whitelist`, `s.SeenSenders`).

In the `MapPatch("/api/settings", ...)` body, delete the four lines assigning
`AutoApproveTrusted`, `PairingMode`, `Whitelist` and `SeenSenders`.

Add the new endpoint at the end of `MapApi`:

```csharp
        app.MapPost("/api/senders/{id:long}/status",
            async (long id, SenderStatusRequest request, StateStore store) =>
            {
                if (!Enum.TryParse<SenderStatus>(request.Status, ignoreCase: true, out var status))
                    return Results.BadRequest(
                        new { error = "status must be known, autoApprove or banned." });

                return await store.MutateAsync(state =>
                {
                    var sender = state.Settings.Senders.FirstOrDefault(s => s.Id == id);
                    if (sender is null)
                    {
                        // Creating on write is how an organiser pre-approves a
                        // photographer, or pre-bans a nuisance, before that person has
                        // ever messaged the bot.
                        sender = new Sender
                        {
                            Id = id, Name = "", FirstSeen = DateTimeOffset.UtcNow,
                        };
                        state.Settings.Senders.Add(sender);
                    }

                    sender.Status = status;

                    // A ban revokes what they already sent, in this same write, so the
                    // screen can never be showing a banned sender's photo between two
                    // state generations.
                    if (status == SenderStatus.Banned)
                    {
                        var now = DateTimeOffset.UtcNow;
                        foreach (var image in state.Images.Values.Where(i => i.SenderId == id))
                        {
                            image.Status = ImageStatus.Rejected;
                            image.DecidedAt = now;
                            ClearTakeoverIfHeldBy(state, image.Id);
                        }
                    }

                    return Results.Ok();
                });
            });
```

Now delete the dead model members. In `src/EventPhotoBot/State/Models.cs`,
remove the `WhitelistEntry` class, the `SeenSender` class, and these four lines
from `Settings`:

```csharp
    public bool AutoApproveTrusted { get; set; }
    public List<WhitelistEntry> Whitelist { get; set; } = [];
    public bool PairingMode { get; set; }
    public List<SeenSender> SeenSenders { get; set; } = [];
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS. Compile errors here mean a reference to a deleted member
survives — fix each at its site rather than restoring the member.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Web/ApiEndpoints.cs src/EventPhotoBot/State/Models.cs tests/EventPhotoBot.Tests/
git commit -m "feat: sender status endpoint with ban cascade; remove the whitelist model"
```

---

### Task 6: Join QR endpoint

**Files:**
- Modify: `src/EventPhotoBot/EventPhotoBot.csproj`
- Create: `src/EventPhotoBot/Web/QrEndpoint.cs`
- Modify: `src/EventPhotoBot/Program.cs`
- Test: `tests/EventPhotoBot.Tests/QrEndpointTests.cs`

**Interfaces:**
- Consumes: `BotIdentity.JoinUrl` (Task 2).
- Produces: `GET /api/join-qr.svg` returning `image/svg+xml`, or 404 when
  `JoinUrl` is null. `QrEndpoint.MapJoinQr(this WebApplication)`.

- [ ] **Step 1: Add the package and confirm its API**

```bash
dotnet add src/EventPhotoBot/EventPhotoBot.csproj package QRCoder
```

Take whatever current stable version NuGet resolves; do not pin an older one.
Confirm the SVG renderer is reachable without `System.Drawing` by building:

```bash
dotnet build src/EventPhotoBot/EventPhotoBot.csproj
```

If `SvgQRCode` is not found, the package split it out — add
`QRCoder.Core` or the package the build error names, and adjust the `using` in
Step 3 accordingly. Everything else in this task is unaffected.

- [ ] **Step 2: Write the failing tests**

Create `tests/EventPhotoBot.Tests/QrEndpointTests.cs`:

```csharp
using System.Net;
using EventPhotoBot.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace EventPhotoBot.Tests;

public class QrEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public QrEndpointTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task The_qr_needs_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_qr_is_served_as_svg_when_the_username_is_known()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_qr_is_absent_when_the_username_is_unknown()
    {
        // A separate factory, so clearing the identity cannot leak into other tests.
        await using var factory = new AppFactory();
        var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<BotIdentity>().Forget();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

`AppFactory` resolves `BotIdentity` from DI, and `ResolveAsync` runs at startup
against `FakeTelegramClient`, whose `Username` defaults to `"eventphotobot"` —
so the second test has a username without extra setup.

- [ ] **Step 3: Implement**

Add a test seam to `src/EventPhotoBot/Telegram/BotIdentity.cs`:

```csharp
    /// <summary>
    /// Drops the resolved username, so a test can exercise the degraded path.
    /// Public rather than internal: the test project is a separate assembly and
    /// this codebase sets no InternalsVisibleTo.
    /// </summary>
    public void Forget() => Username = null;
```

Create `src/EventPhotoBot/Web/QrEndpoint.cs`:

```csharp
using EventPhotoBot.Telegram;
using QRCoder;

namespace EventPhotoBot.Web;

public static class QrEndpoint
{
    public static void MapJoinQr(this WebApplication app)
    {
        app.MapGet("/api/join-qr.svg", (BotIdentity identity) =>
        {
            if (identity.JoinUrl is not { } url) return Results.NotFound();

            // Error correction M: the QR hangs on a wall and may be photographed at an
            // angle or partly glared out. H would be more robust but makes a denser
            // code, which reads worse from the back of a room at this size.
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(4);

            return Results.Text(svg, "image/svg+xml");
        });
    }
}
```

In `src/EventPhotoBot/Program.cs`, add `app.MapJoinQr();` immediately after
`app.MapImages();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/ tests/EventPhotoBot.Tests/QrEndpointTests.cs
git commit -m "feat: serve the join deep link as a QR svg"
```

---

### Task 7: Settings page roster table

**Files:**
- Modify: `src/EventPhotoBot/wwwroot/admin/settings.html`

**Interfaces:**
- Consumes: `GET /api/settings` returning `senders`;
  `POST /api/senders/{id}/status` (Task 5).
- Produces: nothing other tasks consume.

- [ ] **Step 1: Replace the markup**

In `src/EventPhotoBot/wwwroot/admin/settings.html`, delete the pairing banner
(`<div id="pairing-banner" hidden>`), the `pairingMode` checkbox and its label,
the auto-approve checkbox and its label, the `<h3>Seen while pairing</h3>`
heading and the `<table id="seen">`. Rename `<table id="whitelist">` to
`<table id="senders">` under a heading of `<h3>People</h3>`.

- [ ] **Step 2: Replace the script**

Replace the whitelist block of the page script with:

```javascript
    let senders = [];

    const STATUSES = ['known', 'autoApprove', 'banned'];
    const LABELS = { known: 'Review first', autoApprove: 'Auto-approve', banned: 'Banned' };

    function renderSenders() {
      document.getElementById('senders').innerHTML =
        senders.length === 0
          ? '<tr><td class="muted">Nobody yet. People appear here once they scan the QR.</td></tr>'
          : senders.map(s => `
              <tr>
                <td>${s.name || '(no name)'}</td>
                <td class="muted">${s.id}</td>
                <td>
                  <select onchange="setStatus(${s.id}, this.value)">
                    ${STATUSES.map(v =>
                      `<option value="${v}" ${s.status === v ? 'selected' : ''}>${LABELS[v]}</option>`
                    ).join('')}
                  </select>
                </td>
              </tr>`).join('');
    }

    async function setStatus(id, status) {
      if (status === 'banned'
          && !confirm('Ban this person? Every photo they have already sent is rejected, '
                      + 'including any already on the screen. This cannot be undone.')) {
        renderSenders();   // put the dropdown back
        return;
      }
      await Admin.api('POST', `/api/senders/${id}/status`, { status });
      await load();
    }

    async function addSender() {
      const id = Number(prompt('Telegram id to pre-approve:'));
      if (!Number.isSafeInteger(id) || id === 0) return;
      await Admin.api('POST', `/api/senders/${id}/status`, { status: 'autoApprove' });
      await load();
    }
```

In the existing `load()`, replace the whitelist and seen assignments with
`senders = settings.senders ?? [];` and the render calls with
`renderSenders();`. Delete `saveWhitelist`, `toggleTrusted`, `removeEntry`,
`addSeen`, and the `pairingMode` line in the settings-save payload.

The status dropdown reads `s.status` as a camelCase string. If `SenderStatus`
serialises as an integer rather than a string (see Task 3, Step 3), map it here
with `STATUSES[s.status]` instead of comparing directly — check one real
response with `curl` before assuming.

- [ ] **Step 3: Verify by hand**

There is no automated coverage for this page; the suite has none for the other
admin pages either. Run the app against a scratch bucket per the README, open
`/admin/settings`, and confirm: the table lists people, changing a dropdown
persists across a reload, choosing Banned warns first, and Add pre-approves a
typed id.

- [ ] **Step 4: Commit**

```bash
git add src/EventPhotoBot/wwwroot/admin/settings.html
git commit -m "feat: one roster table on the settings page, replacing whitelist and pairing"
```

---

### Task 8: Ban from the approval queue

**Files:**
- Modify: `src/EventPhotoBot/Web/ApiEndpoints.cs`
- Modify: `src/EventPhotoBot/wwwroot/admin/queue.html`
- Test: `tests/EventPhotoBot.Tests/SenderApiTests.cs`

**Interfaces:**
- Consumes: `POST /api/senders/{id}/status` (Task 5).
- Produces: `senderId` on each item from `GET /api/images`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EventPhotoBot.Tests/SenderApiTests.cs`:

```csharp
    [Fact]
    public async Task The_image_list_carries_the_sender_id_so_the_queue_can_ban()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedImageAsync(5008, ImageStatus.Pending);

        var json = await client.GetStringAsync("/api/images?status=pending");

        Assert.Contains("5008", json);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~carries_the_sender_id"`
Expected: FAIL — `/api/images` projects `SenderName` but not `SenderId`.

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/Web/ApiEndpoints.cs`, add `i.SenderId,` to the anonymous
projection in `MapGet("/api/images", ...)`, next to `i.SenderName`.

In `src/EventPhotoBot/wwwroot/admin/queue.html`, add a ban control to each card
whose `senderId` is not null, following the markup the existing approve and
reject buttons use:

```javascript
      const banButton = image.senderId
        ? `<button class="danger" onclick="banSender(${image.senderId})">Ban sender</button>`
        : '';
```

and:

```javascript
    async function banSender(senderId) {
      if (!confirm('Ban this sender? Every photo they have already sent is rejected, '
                   + 'including any already on the screen. This cannot be undone.')) return;
      await Admin.api('POST', `/api/senders/${senderId}/status`, { status: 'banned' });
      await load();
    }
```

Place `${banButton}` alongside the existing buttons in the card template.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS, all tests. Then check the page by hand as in Task 7: banning
from a card removes that sender's photos from the queue.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Web/ApiEndpoints.cs src/EventPhotoBot/wwwroot/admin/queue.html tests/EventPhotoBot.Tests/SenderApiTests.cs
git commit -m "feat: ban a sender from the approval queue"
```

---

### Task 9: QR on the slideshow

**Files:**
- Modify: `src/EventPhotoBot/Web/ManifestBuilder.cs`
- Modify: `src/EventPhotoBot/Web/ApiEndpoints.cs`
- Modify: `src/EventPhotoBot/wwwroot/show.html`
- Modify: `src/EventPhotoBot/wwwroot/show.css`
- Modify: `src/EventPhotoBot/wwwroot/show.js`
- Test: `tests/EventPhotoBot.Tests/ManifestBuilderTests.cs`

**Interfaces:**
- Consumes: `BotIdentity.JoinUrl` (Task 2); `/api/join-qr.svg` (Task 6).
- Produces: `SettingsView.JoinUrl` (`string?`) on the manifest.

- [ ] **Step 1: Write the failing test**

Add to `tests/EventPhotoBot.Tests/ManifestBuilderTests.cs`:

```csharp
[Fact]
public void The_manifest_carries_the_join_url()
{
    var state = new EventState();

    var manifest = ManifestBuilder.Build(
        state, generation: 1, DateTimeOffset.UtcNow, "Party", "https://t.me/bot?start=code");

    Assert.Equal("https://t.me/bot?start=code", manifest.Settings.JoinUrl);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EventPhotoBot.slnx --filter "FullyQualifiedName~The_manifest_carries_the_join_url"`
Expected: FAIL — `Build` takes no fifth argument (compile error).

- [ ] **Step 3: Implement**

In `src/EventPhotoBot/Web/ManifestBuilder.cs`, add the member to `SettingsView`:

```csharp
public sealed record SettingsView(
    int SlideSeconds, int TransitionMs, bool NewestFirstBoost, string Order, string EventName,
    string? JoinUrl);
```

Add a parameter to `Build`:

```csharp
    public static Manifest Build(
        EventState state, long generation, DateTimeOffset now, string eventName = "",
        string? joinUrl = null)
```

and pass it through in the `SettingsView` construction, after `eventName`.

In `src/EventPhotoBot/Web/ApiEndpoints.cs`, add `BotIdentity identity` to the
`/api/manifest` handler's parameters and pass `identity.JoinUrl` as the fifth
argument to `ManifestBuilder.Build`. Add `using EventPhotoBot.Telegram;` if it
is not already there.

The `/api/manifest` ETag still keys off `store.Generation` alone. `JoinUrl` is
fixed for the life of the instance, so it cannot change between two polls of the
same generation — the existing `A_poll_performs_no_object_store_io` guarantee is
unaffected.

In `src/EventPhotoBot/wwwroot/show.html`, add inside `<div id="empty">`, after
the `empty-detail` paragraph:

```html
    <div id="join" hidden>
      <img id="join-qr" alt="Scan to send photos">
      <p id="join-handle"></p>
    </div>
```

and before the keepawake video:

```html
  <img id="join-badge" hidden alt="Scan to send photos">
```

In `src/EventPhotoBot/wwwroot/show.css`, add:

```css
/* Large in the empty state; a small corner badge once photos are showing, so
   somebody arriving late can still join without the screen going idle first. */
#join-qr { width: 30vmin; height: auto; background: #fff; padding: 2vmin; border-radius: 1vmin; }
#join-handle { font-family: monospace; opacity: 0.7; margin-top: 1rem; }
#join-badge {
  position: fixed; right: 2vmin; bottom: 2vmin; width: 11vmin; height: auto;
  background: #fff; padding: 1vmin; border-radius: 1vmin; opacity: 0.85; z-index: 5;
}
```

In `src/EventPhotoBot/wwwroot/show.js`, inside `applyManifest(next)` (around
line 127), immediately after the `emptyEventNameEl.hidden = ...` line near the
top of that function, add:

```javascript
  // The QR is a fixed image for the life of the instance; set src once so the
  // browser is not refetching it on every poll.
    if (next.settings.joinUrl && !joinReady) {
      const handle = '@' + new URL(next.settings.joinUrl).pathname.replace('/', '');
      for (const id of ['join-qr', 'join-badge']) {
        document.getElementById(id).src = '/api/join-qr.svg';
      }
      document.getElementById('join-handle').textContent = handle;
      document.getElementById('join').hidden = false;
      joinReady = true;
    }
    document.getElementById('join-badge').hidden = !joinReady || next.images.length === 0;
```

with `let joinReady = false;` declared beside `const emptyEl = ...` near line 14,
inside the same IIFE scope.

Note `next`, not `manifest` — that is the parameter name `applyManifest` uses.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EventPhotoBot.slnx`
Expected: PASS. Other `ManifestBuilder.Build` callers compile unchanged because
`joinUrl` is optional.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Web/ src/EventPhotoBot/wwwroot/ tests/EventPhotoBot.Tests/ManifestBuilderTests.cs
git commit -m "feat: show the join QR on the slideshow, large when empty and as a badge otherwise"
```

---

### Task 10: Infrastructure for the join code secret

**Files:**
- Modify: `infra/main.tf:102-108` and `infra/main.tf:193-199`
- Modify: `infra/deploy.ps1:60-66`
- Modify: `.github/workflows/deploy.yml`

**Interfaces:**
- Consumes: `AppConfig`'s requirement for `JOIN_CODE` (Task 1).
- Produces: the `eventphoto-join-code` secret and its env projection.

- [ ] **Step 1: Add the secret resource**

In `infra/main.tf`, add to `locals.secret_ids`:

```hcl
    join_code      = "${var.name}-join-code"
```

The `google_secret_manager_secret.secrets` resource and the IAM binding both
use `for_each` over that map, so they pick the new secret up with no other
change. The GitHub workflow's bootstrap step targets
`google_secret_manager_secret.secrets` as a whole for the same reason — **no
change is needed there**, despite what a reading of the design doc's
"bootstrap target list" phrasing might suggest.

- [ ] **Step 2: Project it into the service**

In the `secretEnvironmentVariables` `for_each` map around `infra/main.tf:193`,
add:

```hcl
          JOIN_CODE               = local.secret_ids.join_code
```

- [ ] **Step 3: Print the command in deploy.ps1**

In `infra/deploy.ps1`, after the `cookie-key` line:

```powershell
Write-Host "  printf '%s' 'YOUR_VALUE' | gcloud secrets versions add $Name-join-code      --data-file=- --project $ProjectId"
```

and change the line below it to name the join code as operator-chosen:

```powershell
Write-Host 'Generate the three random ones (webhook-secret, webhook-path, cookie-key) with:  openssl rand -hex 32'
Write-Host 'join-code is yours to choose, and appears in the QR: letters, digits, _ and - only, max 64 characters.'
```

- [ ] **Step 4: Validate**

```bash
terraform -chdir=infra fmt -check
terraform -chdir=infra validate
```

Expected: both clean. `validate` needs an initialised backend; if it complains,
run `terraform -chdir=infra init -backend=false` first, which validates the
configuration without touching remote state.

- [ ] **Step 5: Commit**

```bash
git add infra/ .github/workflows/deploy.yml
git commit -m "feat: provision the join-code secret and project it into the service"
```

---

### Task 11: Runbook

**Files:**
- Modify: `docs/RUNBOOK.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Replace the pairing section**

Delete the "Collecting the whitelist (pairing mode)" section entirely and put
this in its place:

```markdown
### Who can send

Every person the bot knows about has one of three statuses, set from admin
settings or from the approval queue:

- **Review first** — the default for anyone who joins by scanning the QR. Their
  photos land in the approval queue.
- **Auto-approve** — pre-approved photographers. Their photos go straight to the
  screen with no queue step. Set this before the event by adding their Telegram
  id by hand, or promote them once they have scanned.
- **Banned** — messages are dropped silently, and **every photo they already
  sent is rejected in the same action**, including any already on screen. This
  cannot be undone from the UI; un-banning restores their ability to send but
  does not bring the photos back.

Anyone not on the list at all has not scanned the QR. Their photos are declined
with a message pointing at the screen, at most one reply per minute, and nothing
is downloaded or stored.

People join by scanning the QR shown on the slideshow. It encodes
`https://t.me/<bot>?start=<join code>`; scanning opens the bot chat with a Start
button, and tapping it sends the code. That is the whole flow — one scan, one
tap.

The join code is the `eventphoto-join-code` secret, chosen at deploy time.
Changing it means a redeploy, so treat it as fixed once the event starts. It is
not a password: everyone in the room can see the QR, and so can anyone shown a
photo of the screen. It stops someone who merely guesses the bot handle, nothing
more. The banlist is what handles a person you actually want out.
```

- [ ] **Step 2: Update the pre-event checklist**

In "Before the event", replace the whitelist/pairing bullet with:

```markdown
- [ ] Join code chosen and stored:
      `printf '%s' 'YOUR_CODE' | gcloud secrets versions add eventphoto-join-code --data-file=- --project PROJECT_ID`
      — letters, digits, `_` and `-` only, at most 64 characters. Anything else
      fails the deploy, on purpose: Telegram silently drops a deep-link payload
      outside that set.
- [ ] Pre-approved photographers added by Telegram id, set to Auto-approve
- [ ] QR on the slideshow checked from the back of the room, on the actual
      display machine — a QR nobody can scan is the one failure that makes the
      whole join flow useless
```

Add to the bot-creation bullet's list of secrets that the join code is one of
six now, not five.

- [ ] **Step 3: Rewrite the affected acceptance criteria**

Replace the two pairing-mode criteria and the non-whitelisted-sender criterion
with:

```markdown
- [ ] **Scanning the QR admits a new sender.** From a phone that has never
      messaged the bot, scan the QR on the screen and tap Start. Pass: the bot
      replies "You are in", and the phone appears under People in admin settings
      with status "Review first".
- [ ] **A newly admitted sender's photo reaches the queue.** Send one photo from
      that phone. Pass: it appears in `/admin/queue` within five seconds.
- [ ] **An auto-approve sender skips the queue.** Set that phone to
      Auto-approve, send another photo. Pass: it reaches the slideshow without
      an approval step.
- [ ] **Someone who has not scanned is declined and nothing is stored.** From a
      second phone, message the bot directly without scanning. Pass: the reply
      points at the QR, and
      `gcloud storage ls -r gs://BUCKET_NAME/originals` (compared before and
      after) shows no new object.
- [ ] **Banning revokes what a sender already sent.** With one approved photo
      from a test phone on screen, ban that sender from the queue card. Pass:
      the photo leaves the rotation within two seconds, and a further photo from
      that phone produces no reply and no new object.
```

- [ ] **Step 4: Note the mid-event constraint**

Next to the existing mid-event deploy warning under "Killing the instance
mid-event loses nothing", add:

```markdown
Deploying this version over an older one also **empties the People list** —
`state.json` changed shape and there is no migration. Everyone has to re-scan.
That is fine before an event and unacceptable during one, so it belongs on the
same list as the webhook re-registration hazard.
```

- [ ] **Step 5: Commit**

```bash
git add docs/RUNBOOK.md
git commit -m "docs: runbook for the sender roster, join code and banlist"
```

---

### Task 12: Diagrams

**Files:**
- Modify: `docs/diagrams/flows-architecture.html`
- Modify: `docs/diagrams/flows-swimlane.html`
- Modify: `docs/diagrams/flows-sequence.html`
- Create: `docs/diagrams/sender-states.html`

**Interfaces:**
- Consumes: the finished behaviour.
- Produces: nothing.

- [ ] **Step 1: Load the skill and profile**

Use the `diagram-design:diagram-design` skill. The project has no
`.diagram-design` marker; use the saved **bcc** profile at
`~/.diagram-design/profiles/bcc.md`, which is what the existing three files use.
Do not re-run onboarding.

- [ ] **Step 2: Update the three existing diagrams**

Each currently shows the gate model. The changes:

- **flows-architecture.html** — the "Sender's phone" node's sublabel changes
  from `whitelisted` to `scanned the QR`. Add nothing; the architecture is
  unchanged by this work apart from the QR endpoint, which does not earn a node
  at this altitude.
- **flows-swimlane.html** — the "Whitelist + decode" step becomes "Roster +
  decode"; the "Decline reply" branch's sublabel changes from `1 per minute` to
  `scan the QR`; add a second dashed branch to a "Dropped" step for banned
  senders. Re-check the node and arrow budget after adding: 8 nodes, 7 arrows,
  still inside 9 and 12.
- **flows-sequence.html** — no message changes; the `Bot service` actor's
  behaviour is the same sequence. Update the `<desc>` to say the bot checks the
  roster rather than the whitelist.

- [ ] **Step 3: Add the sender-state diagram**

A **state machine** diagram (`references/type-state.md`), slug
`sender-states`, BCC skin, showing: an initial marker into *not on the roster*;
`/start <code>` → **Review first**; admin action → **Auto-approve**; admin
action from either → **Banned** with the transition labelled `REVOKES PHOTOS`;
un-ban back to **Review first**. Coral on **Banned** as the one focal state.
Budget: 3 states, 5 transitions.

- [ ] **Step 4: Verify each file**

```bash
python scripts/self_check.py docs/diagrams/<file>.html
```

Run from the skill's install directory for each of the four files. On Windows,
the Microsoft Store `python` alias does not work — use the real interpreter at
`C:\Users\AkselKvitberg\AppData\Local\Programs\Python\Python314\python.exe`.
Expected: `OK` for all four. Then open each in a browser and confirm no label
sits on a connector and nothing overlaps.

- [ ] **Step 5: Commit**

```bash
git add docs/diagrams/
git commit -m "docs: redraw the flow diagrams for the sender roster, add the state diagram"
```

---

## Done

Run the full suite one last time, then push the branch and open a PR.

```bash
dotnet test EventPhotoBot.slnx
git push -u origin feat/sender-model
```
