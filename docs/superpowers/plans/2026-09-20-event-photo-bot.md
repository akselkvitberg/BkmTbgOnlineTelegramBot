# Event Photo Bot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a single Cloud Run service that ingests photos from a whitelisted Telegram audience, lets one admin approve them, and drives a projector slideshow — deployable and destroyable in a day.

**Architecture:** One ASP.NET Core minimal-API container does everything: Telegram webhook, password-gated admin UI, slideshow page, and image proxying. All metadata lives in a single `state/state.json` object in a GCS bucket, loaded into memory at startup and rewritten on every change with a generation precondition. The slideshow polls `/api/manifest` every two seconds with an ETag; nothing holds a connection open, so Cloud Run scales to zero and bills only request time.

**Tech Stack:** .NET 10 (SDK 10.0.401 confirmed present), ASP.NET Core minimal API, xUnit, SixLabors.ImageSharp, Google.Cloud.Storage.V1, raw `HttpClient` for the Telegram Bot API, vanilla HTML/CSS/JS for the three pages, Terraform for infrastructure.

**Spec:** [telegram-online-bot-spec.md](../../../telegram-online-bot-spec.md)

## Global Constraints

Copied verbatim from the spec. Every task's requirements implicitly include this section.

- **No database.** One object, `state/state.json`, holding `{ "images": { "<id>": {...} }, "settings": {...} }`. Read once at startup, held in memory, whole object rewritten on change.
- **Every state write uses `x-goog-if-generation-match`** set to the generation the in-memory copy was loaded from. On 412: reload, reapply, retry.
- **`state/state-prev.json` is written before each state write.**
- **A manifest poll must never touch GCS.** Compare `If-None-Match` against the stored generation and return a bodyless 304 before doing anything else. The only read is at cold start.
- **The ETag is the `state.json` generation number.** There is no `manifestVersion` field.
- **Write order on ingest:** image objects first, state last. A state write that fails leaves orphaned bytes, never a manifest entry pointing at nothing.
- **No background work.** CPU is allocated only during requests (`cpu_idle = true`). Every handler finishes what it started before returning.
- **Takeover is `settings.takeoverImageId` + `settings.takeoverUntil`, never a per-image flag.** `pin` is `none | recurring` only. Delete and hide must clear takeover if the image held it. Setting takeover on a `pending` image approves it in the same write.
- **The whitelist is `settings.whitelist`, editable at runtime.** Starts empty. No `WHITELIST` environment variable.
- **Every path except `POST /tg/{webhookPath}` and `GET /healthz` requires a valid session.** Unauthenticated pages get the login page; unauthenticated API calls get 401. Image bytes are gated too.
- **Password comparison is constant-time.** Session cookie is signed with `COOKIE_SIGNING_KEY`, HttpOnly, Secure, SameSite=Lax, 14-day lifetime.
- **Derivatives strip EXIF** (GPS coordinates, device identifiers). Originals keep theirs.
- **EXIF orientation is applied before resizing.** Portrait photos landing sideways is the single most common visible bug.
- **The app fails to start if any secret is missing.** Log that a secret loaded, never its value.
- **Secret values never go in Terraform.** Resources in Terraform, versions added with `gcloud secrets versions add`.
- **Region:** `europe-north1`.

## Decisions Locked For This Plan

Three choices the spec leaves open or implies. They are settled here so tasks can be written concretely:

1. **Shuffle is seeded with the generation number.** An unseeded shuffle would reorder the playlist on every poll that misses the ETag. Deterministic per state version means the order only changes when the state does.
2. **Takeover expiry is evaluated client-side.** `takeoverUntil` passing changes what should be on screen but triggers no write, so the ETag would not change. The manifest carries `takeover.until` and the client stops showing it when the time passes. Any subsequent mutation also clears an expired takeover server-side, so state converges.
3. **`newestFirstBoost` is client-side.** The server orders the playlist including recurring interleave; the client inserts newly-approved images near the front on reconcile, per the spec's slideshow section.

## Licensing Flag — Read Before Task 8

SixLabors.ImageSharp v3+ ships under the Six Labors Split License: free for open-source and for organisations under a revenue threshold, commercial licence required otherwise. **Confirm BCC's position before Task 8**, or substitute SkiaSharp (MIT). The pipeline is one file with a single public method, so swapping it is a contained change — but it is cheaper to decide now than after the code exists. This is a flag, not legal advice; check with whoever owns licensing.

## File Structure

```
EventPhotoBot.sln
Dockerfile
.dockerignore
src/EventPhotoBot/
  EventPhotoBot.csproj
  Program.cs                    Host wiring, endpoint mapping, startup state load
  AppConfig.cs                  Env var + secret binding, fail-fast validation
  State/
    Models.cs                   EventState, ImageRecord, Settings, WhitelistEntry, SeenSender, enums
    IObjectStore.cs             Storage abstraction + PreconditionFailedException
    GcsObjectStore.cs           Google.Cloud.Storage.V1 implementation
    StateStore.cs               Load, snapshot, generation, MutateAsync with precondition + retry
  Imaging/
    ImagePipeline.cs            Decode, auto-orient, strip metadata, two derivatives, sha256
  Telegram/
    TelegramModels.cs           Update/Message/PhotoSize/Document DTOs
    ITelegramClient.cs          GetFile, Download, SendMessage, SetWebhook
    TelegramClient.cs           HttpClient implementation
    UpdateHandler.cs            Whitelist, pairing mode, duplicates, album grouping, ingest
  Web/
    SessionCookie.cs            Sign, validate, constant-time password compare
    AuthEndpoints.cs            GET/POST /login, POST /logout
    ManifestBuilder.cs          Visible set, ordering, recurring interleave, takeover, pending count
    ApiEndpoints.cs             Manifest, images, takeover, settings, upload
    ImageEndpoints.cs           /img/{id}/display, /img/{id}/thumb
  wwwroot/
    login.html
    show.html  show.js  show.css
    admin/queue.html  admin/images.html  admin/settings.html
    admin/admin.js  admin/admin.css
tests/EventPhotoBot.Tests/
  EventPhotoBot.Tests.csproj
  Fakes/InMemoryObjectStore.cs
  Fakes/FakeTelegramClient.cs
  StateStoreTests.cs
  ManifestBuilderTests.cs
  ImagePipelineTests.cs
  SessionCookieTests.cs
  UpdateHandlerTests.cs
  TakeoverInvariantTests.cs
  EndpointAuthTests.cs
  TestAssets/portrait-exif-6.jpg  landscape.jpg  tiny.png
infra/
  main.tf  variables.tf  outputs.tf  terraform.tfvars.example
  deploy.ps1
```

Files split by responsibility, not layer: `State/` owns everything about persistence and the shape of stored data; `Telegram/` owns the whole ingest path; `Web/` owns the HTTP surface. `StateStore` is the only thing that writes `state.json`, so the precondition rule has exactly one enforcement point.

---

### Task 1: Solution skeleton, health endpoint, test harness

**Files:**
- Create: `EventPhotoBot.sln`, `src/EventPhotoBot/EventPhotoBot.csproj`, `src/EventPhotoBot/Program.cs`
- Create: `tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj`, `tests/EventPhotoBot.Tests/HealthTests.cs`
- Create: `.gitignore`

**Interfaces:**
- Consumes: nothing.
- Produces: a runnable web app; `WebApplicationFactory<Program>` usable from tests, which requires `public partial class Program { }` at the bottom of `Program.cs`.

- [ ] **Step 1: Create the solution and projects**

```bash
cd /g/bcctbgonline
dotnet new sln -n EventPhotoBot
dotnet new web -n EventPhotoBot -o src/EventPhotoBot -f net10.0
dotnet new xunit -n EventPhotoBot.Tests -o tests/EventPhotoBot.Tests -f net10.0
dotnet sln add src/EventPhotoBot/EventPhotoBot.csproj tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj
dotnet add tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj reference src/EventPhotoBot/EventPhotoBot.csproj
dotnet add tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Add a .gitignore**

```bash
cat > .gitignore <<'EOF'
bin/
obj/
*.user
infra/.terraform/
infra/*.tfstate
infra/*.tfstate.*
infra/terraform.tfvars
EOF
```

Terraform state is ignored because it is local and, per the spec, this infrastructure exists to be destroyed. `terraform.tfvars` is ignored because it will carry the image digest and project id.

- [ ] **Step 3: Write the failing test**

Create `tests/EventPhotoBot.Tests/HealthTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventPhotoBot.Tests;

public class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_ok_without_a_session()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/EventPhotoBot.Tests --filter Healthz_returns_ok_without_a_session`
Expected: FAIL — `Program` is not accessible (the template has top-level statements with an internal generated class), or a 404 on `/healthz`.

- [ ] **Step 5: Write the minimal implementation**

Replace `src/EventPhotoBot/Program.cs` entirely:

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

app.Run();

public partial class Program { }
```

The `public partial class Program { }` is what makes `WebApplicationFactory<Program>` compile. Without it the test project cannot see the generated entry-point type.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/EventPhotoBot.Tests --filter Healthz_returns_ok_without_a_session`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git init
git add .
git commit -m "chore: solution skeleton with health endpoint and test harness"
```

---

### Task 2: State model and the object store abstraction

**Files:**
- Create: `src/EventPhotoBot/State/Models.cs`
- Create: `src/EventPhotoBot/State/IObjectStore.cs`
- Create: `tests/EventPhotoBot.Tests/Fakes/InMemoryObjectStore.cs`
- Create: `tests/EventPhotoBot.Tests/StateModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: the type vocabulary every later task uses —
  `EventState`, `ImageRecord`, `Settings`, `WhitelistEntry`, `SeenSender`,
  `ImageSource`, `ImageStatus`, `PinKind`, `SlideOrder`, `StateJson.Options`,
  `IObjectStore`, `StoredObject`, `PreconditionFailedException`,
  and the test fake `InMemoryObjectStore`.

- [ ] **Step 1: Write the state model**

Create `src/EventPhotoBot/State/Models.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventPhotoBot.State;

public enum ImageSource { Telegram, Admin }
public enum ImageStatus { Pending, Approved, Hidden, Rejected }
public enum PinKind { None, Recurring }
public enum SlideOrder { Shuffle, NewestFirst }

public sealed class ImageRecord
{
    public required string Id { get; set; }
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
}

public sealed class WhitelistEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Trusted { get; set; }
}

public sealed class SeenSender
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
}

public sealed class Settings
{
    public int SlideSeconds { get; set; } = 8;
    public int TransitionMs { get; set; } = 800;
    public SlideOrder Order { get; set; } = SlideOrder.Shuffle;
    public bool NewestFirstBoost { get; set; } = true;
    public int RecurringEvery { get; set; } = 10;
    public string? TakeoverImageId { get; set; }
    public DateTimeOffset? TakeoverUntil { get; set; }
    public bool AutoApproveTrusted { get; set; }
    public List<WhitelistEntry> Whitelist { get; set; } = [];
    public bool PairingMode { get; set; }
    public List<SeenSender> SeenSenders { get; set; } = [];
}

public sealed class EventState
{
    public Dictionary<string, ImageRecord> Images { get; set; } = [];
    public Settings Settings { get; set; } = new();
}

public static class StateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };
}
```

Mutable classes rather than records with `with`-expressions: the state is owned by one process behind one lock and mutated in place, so immutability ceremony would buy nothing. Enums serialise as camelCase strings so the stored object is readable with `gcloud storage cat | jq`, which is the debugging story for this design.

`OriginalExtension` is not in the spec's field table but is needed to reconstruct the `originals/{id}.{ext}` object name for deletion. Without it, deleting an image cannot find its original.

- [ ] **Step 2: Write the object store abstraction**

Create `src/EventPhotoBot/State/IObjectStore.cs`:

```csharp
namespace EventPhotoBot.State;

public sealed record StoredObject(byte[] Bytes, long Generation);

/// <summary>Thrown when an if-generation-match precondition fails (HTTP 412).</summary>
public sealed class PreconditionFailedException(string path)
    : Exception($"Generation precondition failed for '{path}'.");

public interface IObjectStore
{
    /// <summary>Returns null if the object does not exist.</summary>
    Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes and returns the new generation.
    /// ifGenerationMatch: null = unconditional, 0 = must not already exist,
    /// otherwise the generation the caller believes is current.
    /// Throws PreconditionFailedException when the precondition is not met.
    /// </summary>
    Task<long> WriteAsync(string path, byte[] bytes, string contentType,
        long? ifGenerationMatch, CancellationToken ct = default);

    /// <summary>Returns null if the object does not exist.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>No-op if the object does not exist.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the in-memory fake**

Create `tests/EventPhotoBot.Tests/Fakes/InMemoryObjectStore.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Tests.Fakes;

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly Dictionary<string, StoredObject> _objects = [];
    private long _nextGeneration = 1;

    /// <summary>Test hook: number of write calls, to assert polls do no I/O.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Test hook: number of read calls, to assert polls do no I/O.</summary>
    public int ReadCount { get; private set; }

    public IReadOnlyCollection<string> Paths => _objects.Keys;

    public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default)
    {
        ReadCount++;
        return Task.FromResult(_objects.TryGetValue(path, out var o) ? o : null);
    }

    public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
        long? ifGenerationMatch, CancellationToken ct = default)
    {
        WriteCount++;
        if (ifGenerationMatch is { } expected)
        {
            var current = _objects.TryGetValue(path, out var existing) ? existing.Generation : 0L;
            if (current != expected) throw new PreconditionFailedException(path);
        }
        var generation = _nextGeneration++;
        _objects[path] = new StoredObject(bytes, generation);
        return Task.FromResult(generation);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        ReadCount++;
        return Task.FromResult<Stream?>(
            _objects.TryGetValue(path, out var o) ? new MemoryStream(o.Bytes, writable: false) : null);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        _objects.Remove(path);
        return Task.CompletedTask;
    }

    /// <summary>Simulates a concurrent writer bumping the generation behind our back.</summary>
    public void ForceWrite(string path, byte[] bytes) =>
        _objects[path] = new StoredObject(bytes, _nextGeneration++);
}
```

`ForceWrite` exists to test the 412 retry path in Task 3. `ReadCount` and `WriteCount` exist to assert the "a poll must never touch GCS" constraint in Task 7.

- [ ] **Step 4: Write the round-trip test**

Create `tests/EventPhotoBot.Tests/StateModelTests.cs`:

```csharp
using System.Text.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class StateModelTests
{
    [Fact]
    public void State_round_trips_through_json_with_camel_case_enums()
    {
        var state = new EventState();
        state.Settings.Whitelist.Add(new WhitelistEntry { Id = 42, Name = "Ada", Trusted = true });
        state.Settings.TakeoverImageId = "01ABC";
        state.Images["01ABC"] = new ImageRecord
        {
            Id = "01ABC",
            Source = ImageSource.Telegram,
            Sha256 = "deadbeef",
            Status = ImageStatus.Approved,
            Pin = PinKind.Recurring,
            SortKey = "2026-09-20T18:00:00Z",
            OriginalExtension = "jpg",
        };

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var back = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        Assert.Contains("\"telegram\"", json);
        Assert.Contains("\"recurring\"", json);
        Assert.Equal(ImageStatus.Approved, back.Images["01ABC"].Status);
        Assert.Equal("Ada", back.Settings.Whitelist[0].Name);
        Assert.True(back.Settings.Whitelist[0].Trusted);
        Assert.Equal("01ABC", back.Settings.TakeoverImageId);
    }

    [Fact]
    public void Fresh_state_has_an_empty_whitelist_and_pairing_off()
    {
        var state = new EventState();
        Assert.Empty(state.Settings.Whitelist);
        Assert.False(state.Settings.PairingMode);
    }
}
```

The second test pins the spec's safe default: a fresh deployment accepts nothing until someone is added, and pairing mode is off.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter StateModelTests`
Expected: PASS (these are characterisation tests for code written in the same task; they fail only if the model is wrong).

- [ ] **Step 6: Commit**

```bash
git add src/EventPhotoBot/State tests/EventPhotoBot.Tests
git commit -m "feat: state model, object store abstraction and in-memory fake"
```

---

### Task 3: StateStore — load, mutate, generation precondition, retry

This is the task that enforces the spec's central durability rule. Everything else depends on it being right.

**Files:**
- Create: `src/EventPhotoBot/State/StateStore.cs`
- Create: `tests/EventPhotoBot.Tests/StateStoreTests.cs`

**Interfaces:**
- Consumes: `IObjectStore`, `EventState`, `StateJson.Options` from Task 2.
- Produces:
  - `StateStore.StatePath` = `"state/state.json"`, `StateStore.PrevPath` = `"state/state-prev.json"`
  - `Task LoadAsync(CancellationToken ct = default)`
  - `EventState Snapshot { get; }`
  - `long Generation { get; }`
  - `Task<T> MutateAsync<T>(Func<EventState, T> mutate, CancellationToken ct = default)`
  - `Task MutateAsync(Action<EventState> mutate, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing tests**

Create `tests/EventPhotoBot.Tests/StateStoreTests.cs`:

```csharp
using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class StateStoreTests
{
    private static (StateStore Store, InMemoryObjectStore Objects) NewStore()
    {
        var objects = new InMemoryObjectStore();
        return (new StateStore(objects), objects);
    }

    [Fact]
    public async Task Load_on_an_empty_bucket_starts_from_defaults()
    {
        var (store, _) = NewStore();
        await store.LoadAsync();

        Assert.Empty(store.Snapshot.Images);
        Assert.Equal(0, store.Generation);
    }

    [Fact]
    public async Task Mutate_writes_state_and_advances_the_generation()
    {
        var (store, objects) = NewStore();
        await store.LoadAsync();

        await store.MutateAsync(s => s.Settings.SlideSeconds = 12);

        Assert.Equal(12, store.Snapshot.Settings.SlideSeconds);
        Assert.True(store.Generation > 0);
        Assert.Contains(StateStore.StatePath, objects.Paths);
    }

    [Fact]
    public async Task Mutate_writes_the_previous_generation_to_the_backup_path()
    {
        var (store, objects) = NewStore();
        await store.LoadAsync();

        await store.MutateAsync(s => s.Settings.SlideSeconds = 11);
        await store.MutateAsync(s => s.Settings.SlideSeconds = 22);

        var prev = await objects.ReadAsync(StateStore.PrevPath);
        Assert.NotNull(prev);
        var recovered = JsonSerializer.Deserialize<EventState>(prev!.Bytes, StateJson.Options)!;
        Assert.Equal(11, recovered.Settings.SlideSeconds);
    }

    [Fact]
    public async Task A_second_store_sees_what_the_first_one_wrote()
    {
        var objects = new InMemoryObjectStore();
        var first = new StateStore(objects);
        await first.LoadAsync();
        await first.MutateAsync(s => s.Settings.RecurringEvery = 5);

        var second = new StateStore(objects);
        await second.LoadAsync();

        Assert.Equal(5, second.Snapshot.Settings.RecurringEvery);
        Assert.Equal(first.Generation, second.Generation);
    }

    [Fact]
    public async Task An_interleaved_write_is_detected_and_the_mutation_is_reapplied()
    {
        var (store, objects) = NewStore();
        await store.LoadAsync();
        await store.MutateAsync(s => s.Settings.SlideSeconds = 8);

        // Someone else rewrites state.json behind our back, bumping the generation.
        var theirs = new EventState();
        theirs.Settings.RecurringEvery = 99;
        objects.ForceWrite(StateStore.StatePath,
            JsonSerializer.SerializeToUtf8Bytes(theirs, StateJson.Options));

        await store.MutateAsync(s => s.Settings.SlideSeconds = 15);

        // Our change landed, and theirs was not silently discarded.
        Assert.Equal(15, store.Snapshot.Settings.SlideSeconds);
        Assert.Equal(99, store.Snapshot.Settings.RecurringEvery);
    }

    [Fact]
    public async Task Concurrent_mutations_are_serialised_and_none_are_lost()
    {
        var (store, _) = NewStore();
        await store.LoadAsync();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.MutateAsync(s => s.Images[$"img{i}"] = new ImageRecord
            {
                Id = $"img{i}",
                Sha256 = $"hash{i}",
                SortKey = $"{i:D4}",
                OriginalExtension = "jpg",
            })));

        Assert.Equal(50, store.Snapshot.Images.Count);
    }

    [Fact]
    public async Task Mutate_returns_the_value_the_mutation_produced()
    {
        var (store, _) = NewStore();
        await store.LoadAsync();

        var count = await store.MutateAsync(s =>
        {
            s.Settings.SlideSeconds = 9;
            return s.Settings.SlideSeconds;
        });

        Assert.Equal(9, count);
    }
}
```

The interleaved-write test is the important one. It asserts the difference between a lost update and a detected one: after the retry, both writers' changes are present.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter StateStoreTests`
Expected: FAIL to compile — `StateStore` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/EventPhotoBot/State/StateStore.cs`:

```csharp
using System.Text.Json;

namespace EventPhotoBot.State;

/// <summary>
/// Owns the single source of truth. All state lives in memory; every change
/// rewrites state/state.json with an if-generation-match precondition, so an
/// interleaved write is detected and retried rather than silently overwritten.
/// This is the only type that writes state.json.
/// </summary>
public sealed class StateStore(IObjectStore objects, ILogger<StateStore>? logger = null)
{
    public const string StatePath = "state/state.json";
    public const string PrevPath = "state/state-prev.json";
    private const int MaxAttempts = 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EventState _state = new();
    private byte[] _lastBytes = [];
    private long _generation;

    /// <summary>The live state. Read freely; mutate only through MutateAsync.</summary>
    public EventState Snapshot => _state;

    /// <summary>The GCS generation of state.json, used directly as the manifest ETag.</summary>
    public long Generation => _generation;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var stored = await objects.ReadAsync(StatePath, ct);
        if (stored is null)
        {
            _state = new EventState();
            _lastBytes = [];
            _generation = 0;
            logger?.LogInformation("No existing state found; starting from defaults.");
            return;
        }

        _state = JsonSerializer.Deserialize<EventState>(stored.Bytes, StateJson.Options)
                 ?? new EventState();
        _lastBytes = stored.Bytes;
        _generation = stored.Generation;
        logger?.LogInformation("Loaded state at generation {Generation} with {Count} images.",
            _generation, _state.Images.Count);
    }

    public async Task MutateAsync(Action<EventState> mutate, CancellationToken ct = default) =>
        await MutateAsync<object?>(s => { mutate(s); return null; }, ct);

    public async Task<T> MutateAsync<T>(Func<EventState, T> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                ClearExpiredTakeover(_state);
                var result = mutate(_state);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(_state, StateJson.Options);

                try
                {
                    // Backup first: if the state write then fails, the previous
                    // generation is still one object away.
                    if (_lastBytes.Length > 0)
                        await objects.WriteAsync(PrevPath, _lastBytes, "application/json", null, ct);

                    _generation = await objects.WriteAsync(
                        StatePath, bytes, "application/json",
                        ifGenerationMatch: _generation, ct);
                    _lastBytes = bytes;
                    return result;
                }
                catch (PreconditionFailedException) when (attempt < MaxAttempts)
                {
                    logger?.LogWarning(
                        "State generation moved under us (attempt {Attempt}); reloading and reapplying.",
                        attempt);
                    await LoadAsync(ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takeover expiry triggers no write of its own, so the client drops an expired
    /// takeover on its own clock. This converges the stored state on the next change.
    /// </summary>
    private static void ClearExpiredTakeover(EventState state)
    {
        var s = state.Settings;
        if (s.TakeoverImageId is null) return;
        if (s.TakeoverUntil is { } until && until <= DateTimeOffset.UtcNow)
        {
            s.TakeoverImageId = null;
            s.TakeoverUntil = null;
        }
    }
}
```

Two details worth understanding rather than copying blindly:

The mutation lambda is reapplied to freshly loaded state after a 412, which is why it must be written as an operation on whatever state it is handed, not as a computation over state captured beforehand. Every caller in this plan follows that rule.

`ClearExpiredTakeover` runs before every mutation, which is decision 2 from the top of this plan: expiry is primarily the client's job, and this is the server-side convergence half of it.

- [ ] **Step 4: Register the logger dependency**

`StateStore` takes `ILogger<StateStore>?`. The test constructs it with one argument, so the parameter must stay optional. No change needed beyond confirming `Microsoft.Extensions.Logging.Abstractions` is available — it is, transitively, in a web project.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter StateStoreTests`
Expected: PASS, all seven.

- [ ] **Step 6: Commit**

```bash
git add src/EventPhotoBot/State/StateStore.cs tests/EventPhotoBot.Tests/StateStoreTests.cs
git commit -m "feat: state store with generation precondition, backup and retry"
```

---

### Task 4: GCS object store

**Files:**
- Create: `src/EventPhotoBot/State/GcsObjectStore.cs`
- Modify: `src/EventPhotoBot/EventPhotoBot.csproj` (add package)

**Interfaces:**
- Consumes: `IObjectStore`, `StoredObject`, `PreconditionFailedException` from Task 2.
- Produces: `GcsObjectStore(string bucketName, StorageClient client, ILogger<GcsObjectStore>? logger = null)`.

There is no unit test here: the whole type is a thin translation of one SDK's API into another's, so a test would assert the mock matches the implementation and nothing more. It is exercised end to end by the smoke test in Task 16.

- [ ] **Step 1: Add the package**

```bash
dotnet add src/EventPhotoBot/EventPhotoBot.csproj package Google.Cloud.Storage.V1
```

- [ ] **Step 2: Write the implementation**

Create `src/EventPhotoBot/State/GcsObjectStore.cs`:

```csharp
using Google;
using Google.Cloud.Storage.V1;
using System.Net;

namespace EventPhotoBot.State;

public sealed class GcsObjectStore(
    string bucketName,
    StorageClient client,
    ILogger<GcsObjectStore>? logger = null) : IObjectStore
{
    public async Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        try
        {
            var obj = await client.DownloadObjectAsync(bucketName, path, buffer, cancellationToken: ct);
            return new StoredObject(buffer.ToArray(), (long)(obj.Generation ?? 0));
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<long> WriteAsync(string path, byte[] bytes, string contentType,
        long? ifGenerationMatch, CancellationToken ct = default)
    {
        var options = ifGenerationMatch is { } generation
            ? new UploadObjectOptions { IfGenerationMatch = generation }
            : null;

        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var obj = await client.UploadObjectAsync(
                bucketName, path, contentType, source, options, ct);
            return (long)(obj.Generation ?? 0);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            logger?.LogWarning("Precondition failed writing {Path}.", path);
            throw new PreconditionFailedException(path);
        }
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();
        try
        {
            await client.DownloadObjectAsync(bucketName, path, buffer, cancellationToken: ct);
            buffer.Position = 0;
            return buffer;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await buffer.DisposeAsync();
            return null;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await client.DeleteObjectAsync(bucketName, path, cancellationToken: ct);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Deleting something already gone is the desired end state.
        }
    }
}
```

`IfGenerationMatch = 0` is GCS's "must not already exist", which is exactly what `StateStore` passes on the very first write when `_generation` is still 0. The semantics line up without a special case.

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/EventPhotoBot`
Expected: build succeeds with no warnings about the `Google` namespace.

- [ ] **Step 4: Commit**

```bash
git add src/EventPhotoBot/State/GcsObjectStore.cs src/EventPhotoBot/EventPhotoBot.csproj
git commit -m "feat: GCS implementation of the object store"
```

---

### Task 5: Configuration and fail-fast secret loading

**Files:**
- Create: `src/EventPhotoBot/AppConfig.cs`
- Create: `tests/EventPhotoBot.Tests/AppConfigTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AppConfig` with properties `BucketName`, `EventName`, `BotToken`, `WebhookSecret`, `WebhookPath`, `AdminPassword`, `CookieSigningKey`; and `static AppConfig Load(IConfiguration config)` which throws `InvalidOperationException` naming every missing key.

- [ ] **Step 1: Write the failing test**

Create `tests/EventPhotoBot.Tests/AppConfigTests.cs`:

```csharp
using EventPhotoBot;
using Microsoft.Extensions.Configuration;

namespace EventPhotoBot.Tests;

public class AppConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static (string, string)[] Complete() =>
    [
        ("BUCKET_NAME", "bucket"),
        ("EVENT_NAME", "Party"),
        ("TELEGRAM_BOT_TOKEN", "token"),
        ("TELEGRAM_WEBHOOK_SECRET", "secret"),
        ("TELEGRAM_WEBHOOK_PATH", "abc123"),
        ("ADMIN_PASSWORD", "hunter2"),
        ("COOKIE_SIGNING_KEY", "0123456789abcdef0123456789abcdef"),
    ];

    [Fact]
    public void Load_binds_every_value()
    {
        var config = AppConfig.Load(Config(Complete()));

        Assert.Equal("bucket", config.BucketName);
        Assert.Equal("Party", config.EventName);
        Assert.Equal("abc123", config.WebhookPath);
    }

    [Fact]
    public void Load_throws_and_names_every_missing_key()
    {
        var partial = Complete().Where(p =>
            p.Item1 is not ("ADMIN_PASSWORD" or "COOKIE_SIGNING_KEY")).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(partial)));

        Assert.Contains("ADMIN_PASSWORD", error.Message);
        Assert.Contains("COOKIE_SIGNING_KEY", error.Message);
    }

    [Fact]
    public void Load_treats_an_empty_string_as_missing()
    {
        var blanked = Complete()
            .Select(p => p.Item1 == "TELEGRAM_BOT_TOKEN" ? (p.Item1, "") : p).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(blanked)));

        Assert.Contains("TELEGRAM_BOT_TOKEN", error.Message);
    }
}
```

The empty-string test matters because a Secret Manager version that was created empty binds as `""`, not null. Starting up with a blank bot token is exactly the degraded state the spec forbids.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/EventPhotoBot.Tests --filter AppConfigTests`
Expected: FAIL to compile — `AppConfig` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/EventPhotoBot/AppConfig.cs`:

```csharp
namespace EventPhotoBot;

/// <summary>
/// Every value arrives as an environment variable; the secrets among them are
/// projected from Secret Manager by Cloud Run. Missing anything is fatal at
/// startup rather than at the first request that needs it.
/// </summary>
public sealed class AppConfig
{
    public required string BucketName { get; init; }
    public required string EventName { get; init; }
    public required string BotToken { get; init; }
    public required string WebhookSecret { get; init; }
    public required string WebhookPath { get; init; }
    public required string AdminPassword { get; init; }
    public required string CookieSigningKey { get; init; }

    private static readonly string[] SecretKeys =
    [
        "TELEGRAM_BOT_TOKEN", "TELEGRAM_WEBHOOK_SECRET", "TELEGRAM_WEBHOOK_PATH",
        "ADMIN_PASSWORD", "COOKIE_SIGNING_KEY",
    ];

    public static AppConfig Load(IConfiguration config)
    {
        string[] required =
        [
            "BUCKET_NAME", "EVENT_NAME", .. SecretKeys,
        ];

        var missing = required.Where(k => string.IsNullOrWhiteSpace(config[k])).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required configuration: {string.Join(", ", missing)}. " +
                "Secrets come from Secret Manager via Cloud Run; check the service's env vars.");
        }

        return new AppConfig
        {
            BucketName = config["BUCKET_NAME"]!,
            EventName = config["EVENT_NAME"]!,
            BotToken = config["TELEGRAM_BOT_TOKEN"]!,
            WebhookSecret = config["TELEGRAM_WEBHOOK_SECRET"]!,
            WebhookPath = config["TELEGRAM_WEBHOOK_PATH"]!,
            AdminPassword = config["ADMIN_PASSWORD"]!,
            CookieSigningKey = config["COOKIE_SIGNING_KEY"]!,
        };
    }

    /// <summary>Logs that each secret loaded. Never logs a value.</summary>
    public void LogLoaded(ILogger logger)
    {
        foreach (var key in SecretKeys) logger.LogInformation("Secret {Key} loaded.", key);
        logger.LogInformation("Bucket {Bucket}, event {Event}.", BucketName, EventName);
    }
}
```

`LogLoaded` takes the keys, never the values, so there is no code path where a secret can reach a log line.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter AppConfigTests`
Expected: PASS, all three.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/AppConfig.cs tests/EventPhotoBot.Tests/AppConfigTests.cs
git commit -m "feat: fail-fast configuration loading"
```

---

### Task 6: Session cookie, constant-time password check, login endpoints

**Files:**
- Create: `src/EventPhotoBot/Web/SessionCookie.cs`
- Create: `src/EventPhotoBot/Web/AuthEndpoints.cs`
- Create: `src/EventPhotoBot/wwwroot/login.html`
- Create: `tests/EventPhotoBot.Tests/SessionCookieTests.cs`

**Interfaces:**
- Consumes: `AppConfig` from Task 5.
- Produces:
  - `SessionCookie.Name` = `"eventphoto_session"`
  - `static string Issue(string signingKey, DateTimeOffset expiresAt)`
  - `static bool IsValid(string signingKey, string? cookieValue, DateTimeOffset now)`
  - `static bool PasswordMatches(string expected, string? supplied)`
  - `static void MapAuth(this WebApplication app, AppConfig config)` mapping `GET /login`, `POST /login`, `POST /logout`
  - `SessionCookie.Lifetime` = `TimeSpan.FromDays(14)`

Hand-rolled rather than ASP.NET Core cookie authentication with Data Protection: Data Protection keys regenerate in an ephemeral container, which would log everyone out on every cold start. `COOKIE_SIGNING_KEY` exists in the spec precisely to survive that, and an HMAC over an expiry stamp is the whole requirement.

- [ ] **Step 1: Write the failing test**

Create `tests/EventPhotoBot.Tests/SessionCookieTests.cs`:

```csharp
using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class SessionCookieTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_freshly_issued_cookie_is_valid()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddDays(14));
        Assert.True(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void An_expired_cookie_is_rejected()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddMinutes(-1));
        Assert.False(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void A_cookie_signed_with_a_different_key_is_rejected()
    {
        var cookie = SessionCookie.Issue("ffffffffffffffffffffffffffffffff", Now.AddDays(1));
        Assert.False(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void A_tampered_expiry_is_rejected()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddMinutes(-1));
        var forged = $"{Now.AddDays(1).ToUnixTimeSeconds()}.{cookie.Split('.')[1]}";
        Assert.False(SessionCookie.IsValid(Key, forged, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("123")]
    [InlineData("notanumber.abcd")]
    public void Malformed_cookies_are_rejected_without_throwing(string? value)
    {
        Assert.False(SessionCookie.IsValid(Key, value, Now));
    }

    [Fact]
    public void Password_comparison_accepts_the_right_password()
    {
        Assert.True(SessionCookie.PasswordMatches("hunter2", "hunter2"));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("hunter")]
    [InlineData("hunter22")]
    [InlineData("")]
    [InlineData(null)]
    public void Password_comparison_rejects_everything_else(string? supplied)
    {
        Assert.False(SessionCookie.PasswordMatches("hunter2", supplied));
    }
}
```

The tampered-expiry test is the one that would catch a signature covering only part of the cookie.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/EventPhotoBot.Tests --filter SessionCookieTests`
Expected: FAIL to compile — `SessionCookie` does not exist.

- [ ] **Step 3: Write the cookie implementation**

Create `src/EventPhotoBot/Web/SessionCookie.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace EventPhotoBot.Web;

/// <summary>
/// A session is "someone typed the password". There is one user, so the cookie
/// carries an expiry and an HMAC over it, and nothing else.
/// </summary>
public static class SessionCookie
{
    public const string Name = "eventphoto_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static string Issue(string signingKey, DateTimeOffset expiresAt)
    {
        var payload = expiresAt.ToUnixTimeSeconds().ToString();
        return $"{payload}.{Sign(signingKey, payload)}";
    }

    public static bool IsValid(string signingKey, string? cookieValue, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieValue)) return false;

        var separator = cookieValue.IndexOf('.');
        if (separator <= 0 || separator == cookieValue.Length - 1) return false;

        var payload = cookieValue[..separator];
        var signature = cookieValue[(separator + 1)..];

        if (!long.TryParse(payload, out var expiresAtUnix)) return false;

        var expected = Sign(signingKey, payload);
        if (!FixedTimeEquals(expected, signature)) return false;

        return DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) > now;
    }

    public static bool PasswordMatches(string expected, string? supplied) =>
        supplied is not null && FixedTimeEquals(expected, supplied);

    private static string Sign(string signingKey, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Hashes both sides before comparing so the comparison is constant time
    /// regardless of length — FixedTimeEquals alone leaks length via its argument check.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
```

`CryptographicOperations.FixedTimeEquals` returns `false` immediately when lengths differ, which leaks the password length through timing. Hashing both operands to a fixed 32 bytes first removes that.

- [ ] **Step 4: Write the login page**

Create `src/EventPhotoBot/wwwroot/login.html`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Sign in</title>
  <style>
    :root { color-scheme: light dark; }
    body { margin: 0; min-height: 100dvh; display: grid; place-items: center;
           font: 16px/1.5 system-ui, sans-serif; background: #111; color: #eee; padding: 16px; }
    form { display: grid; gap: 12px; width: min(320px, 100%); }
    h1 { font-size: 1.25rem; margin: 0 0 4px; font-weight: 600; }
    input, button { font: inherit; padding: 12px; border-radius: 8px; border: 1px solid #444; }
    input { background: #1c1c1c; color: inherit; }
    button { background: #2f6fed; color: #fff; border-color: transparent; cursor: pointer; }
    .error { color: #ff8a8a; font-size: 0.9rem; min-height: 1.5em; }
  </style>
</head>
<body>
  <form method="post" action="/login">
    <h1>Event photos</h1>
    <label for="password">Password</label>
    <input id="password" name="password" type="password" autocomplete="current-password"
           autofocus required>
    <button type="submit">Sign in</button>
    <p class="error" id="error"></p>
  </form>
  <script>
    const reason = new URLSearchParams(location.search).get('error');
    if (reason === 'bad') document.getElementById('error').textContent = 'Wrong password.';
    if (reason === 'rate') document.getElementById('error').textContent =
      'Too many attempts. Wait a minute and try again.';
  </script>
</body>
</html>
```

- [ ] **Step 5: Write the auth endpoints and the gate**

Create `src/EventPhotoBot/Web/AuthEndpoints.cs`:

```csharp
using System.Threading.RateLimiting;

namespace EventPhotoBot.Web;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void AddLoginRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 8,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

    public static void MapAuth(this WebApplication app, AppConfig config)
    {
        app.MapGet("/login", () => Results.File(
            Path.Combine(app.Environment.WebRootPath, "login.html"), "text/html"));

        app.MapPost("/login", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (!SessionCookie.PasswordMatches(config.AdminPassword, form["password"]))
                return Results.Redirect("/login?error=bad");

            var expiresAt = DateTimeOffset.UtcNow.Add(SessionCookie.Lifetime);
            http.Response.Cookies.Append(
                SessionCookie.Name,
                SessionCookie.Issue(config.CookieSigningKey, expiresAt),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = expiresAt,
                    Path = "/",
                });
            return Results.Redirect("/show");
        }).RequireRateLimiting(LoginRateLimitPolicy);

        app.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(SessionCookie.Name);
            return Results.Redirect("/login");
        });
    }

    /// <summary>
    /// The gate. Everything except the webhook, the health probe and the login
    /// surface needs a session; API calls get 401, pages get the login form.
    /// </summary>
    public static void UseSessionGate(this WebApplication app, AppConfig config)
    {
        var open = new HashSet<string>(StringComparer.Ordinal)
        {
            "/healthz", "/login", $"/tg/{config.WebhookPath}",
        };

        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value ?? "/";
            if (open.Contains(path))
            {
                await next();
                return;
            }

            var cookie = http.Request.Cookies[SessionCookie.Name];
            if (SessionCookie.IsValid(config.CookieSigningKey, cookie, DateTimeOffset.UtcNow))
            {
                await next();
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal)
                || path.StartsWith("/img/", StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            http.Response.Redirect("/login");
        });
    }
}
```

The gate is a middleware over everything rather than per-endpoint metadata, because the spec's requirement is "every path except these three" — a default-open list inverted by hand would eventually miss one, and the one it missed would be an image route serving bytes to strangers.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter SessionCookieTests`
Expected: PASS, all twelve cases.

- [ ] **Step 7: Commit**

```bash
git add src/EventPhotoBot/Web src/EventPhotoBot/wwwroot/login.html tests/EventPhotoBot.Tests/SessionCookieTests.cs
git commit -m "feat: session cookie, constant-time password check and the session gate"
```

---

### Task 7: Manifest builder

The slideshow's whole behaviour comes from this one pure function, which makes it the highest-value thing in the plan to test properly.

**Files:**
- Create: `src/EventPhotoBot/Web/ManifestBuilder.cs`
- Create: `tests/EventPhotoBot.Tests/ManifestBuilderTests.cs`

**Interfaces:**
- Consumes: `EventState`, `ImageRecord`, `Settings`, `ImageStatus`, `PinKind`, `SlideOrder` from Task 2.
- Produces:
  - `record ManifestImage(string Id, int Width, int Height, string? Caption, string? SenderName, bool Recurring)`
  - `record TakeoverView(string Id, DateTimeOffset? Until)`
  - `record SettingsView(int SlideSeconds, int TransitionMs, bool NewestFirstBoost, string Order)`
  - `record Manifest(long Version, IReadOnlyList<ManifestImage> Images, TakeoverView? Takeover, SettingsView Settings, int PendingCount)`
  - `static Manifest Build(EventState state, long generation, DateTimeOffset now)`

- [ ] **Step 1: Write the failing tests**

Create `tests/EventPhotoBot.Tests/ManifestBuilderTests.cs`:

```csharp
using EventPhotoBot.State;
using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class ManifestBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 20, 0, 0, TimeSpan.Zero);

    private static EventState StateWith(params (string Id, ImageStatus Status, PinKind Pin)[] images)
    {
        var state = new EventState();
        var n = 0;
        foreach (var (id, status, pin) in images)
        {
            state.Images[id] = new ImageRecord
            {
                Id = id,
                Status = status,
                Pin = pin,
                Sha256 = id,
                Width = 100,
                Height = 100,
                SortKey = $"{n:D4}",
                ReceivedAt = Now.AddMinutes(n),
                OriginalExtension = "jpg",
            };
            n++;
        }
        return state;
    }

    [Fact]
    public void Only_approved_images_reach_the_playlist()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Pending, PinKind.None),
            ("c", ImageStatus.Hidden, PinKind.None),
            ("d", ImageStatus.Rejected, PinKind.None));

        var manifest = ManifestBuilder.Build(state, 7, Now);

        Assert.Equal(["a"], manifest.Images.Select(i => i.Id));
    }

    [Fact]
    public void Pending_count_counts_only_pending_images()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Pending, PinKind.None),
            ("c", ImageStatus.Pending, PinKind.None),
            ("d", ImageStatus.Rejected, PinKind.None));

        Assert.Equal(2, ManifestBuilder.Build(state, 1, Now).PendingCount);
    }

    [Fact]
    public void Version_is_the_generation_it_was_given()
    {
        Assert.Equal(42, ManifestBuilder.Build(new EventState(), 42, Now).Version);
    }

    [Fact]
    public void Newest_first_ordering_puts_the_most_recent_first()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None),
            ("c", ImageStatus.Approved, PinKind.None));
        state.Settings.Order = SlideOrder.NewestFirst;

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.Equal(["c", "b", "a"], manifest.Images.Select(i => i.Id));
    }

    [Fact]
    public void Shuffle_is_stable_for_a_given_generation_and_changes_with_it()
    {
        var state = StateWith(Enumerable.Range(0, 20)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None)).ToArray());
        state.Settings.Order = SlideOrder.Shuffle;

        var first = ManifestBuilder.Build(state, 5, Now).Images.Select(i => i.Id).ToArray();
        var again = ManifestBuilder.Build(state, 5, Now).Images.Select(i => i.Id).ToArray();
        var later = ManifestBuilder.Build(state, 6, Now).Images.Select(i => i.Id).ToArray();

        Assert.Equal(first, again);
        Assert.NotEqual(first, later);
        Assert.Equal(first.Order(), later.Order());
    }

    [Fact]
    public void Recurring_pins_are_interleaved_at_the_configured_interval()
    {
        var images = Enumerable.Range(0, 9)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None))
            .Append(("menu", ImageStatus.Approved, PinKind.Recurring))
            .ToArray();
        var state = StateWith(images);
        state.Settings.Order = SlideOrder.NewestFirst;
        state.Settings.RecurringEvery = 3;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();

        // The menu appears after every third ordinary image, and is flagged.
        Assert.Equal("menu", ids[3]);
        Assert.Equal("menu", ids[7]);
        Assert.DoesNotContain("menu", ids.Take(3));
        Assert.True(ManifestBuilder.Build(state, 1, Now)
            .Images.First(i => i.Id == "menu").Recurring);
    }

    [Fact]
    public void Several_recurring_pins_rotate_through_the_slot()
    {
        var images = Enumerable.Range(0, 6)
            .Select(i => ($"img{i}", ImageStatus.Approved, PinKind.None))
            .Concat([("menu", ImageStatus.Approved, PinKind.Recurring),
                     ("programme", ImageStatus.Approved, PinKind.Recurring)])
            .ToArray();
        var state = StateWith(images);
        state.Settings.Order = SlideOrder.NewestFirst;
        state.Settings.RecurringEvery = 2;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();
        var pinned = ids.Where(id => id is "menu" or "programme").ToArray();

        Assert.True(pinned.Length >= 2);
        Assert.NotEqual(pinned[0], pinned[1]);
    }

    [Fact]
    public void A_recurring_pin_is_not_also_listed_as_an_ordinary_image()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("menu", ImageStatus.Approved, PinKind.Recurring));
        state.Settings.RecurringEvery = 10;

        var ids = ManifestBuilder.Build(state, 1, Now).Images.Select(i => i.Id).ToArray();

        Assert.Single(ids.Where(id => id == "menu"));
    }

    [Fact]
    public void An_active_takeover_is_reported_with_its_expiry()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = Now.AddMinutes(15);

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.NotNull(manifest.Takeover);
        Assert.Equal("a", manifest.Takeover!.Id);
        Assert.Equal(Now.AddMinutes(15), manifest.Takeover.Until);
    }

    [Fact]
    public void A_takeover_with_no_expiry_runs_until_cleared()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = null;

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.NotNull(manifest.Takeover);
        Assert.Null(manifest.Takeover!.Until);
    }

    [Fact]
    public void An_expired_takeover_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";
        state.Settings.TakeoverUntil = Now.AddMinutes(-1);

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void A_takeover_pointing_at_a_missing_image_is_not_reported()
    {
        var state = StateWith(("a", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "gone";

        Assert.Null(ManifestBuilder.Build(state, 1, Now).Takeover);
    }

    [Fact]
    public void The_playlist_is_still_built_while_a_takeover_is_active()
    {
        var state = StateWith(
            ("a", ImageStatus.Approved, PinKind.None),
            ("b", ImageStatus.Approved, PinKind.None));
        state.Settings.TakeoverImageId = "a";

        var manifest = ManifestBuilder.Build(state, 1, Now);

        Assert.Equal(2, manifest.Images.Count);
    }

    [Fact]
    public void An_empty_event_produces_an_empty_playlist_rather_than_throwing()
    {
        var manifest = ManifestBuilder.Build(new EventState(), 0, Now);

        Assert.Empty(manifest.Images);
        Assert.Null(manifest.Takeover);
        Assert.Equal(0, manifest.PendingCount);
    }
}
```

The last test is the empty-state case the spec calls for on the slideshow: a holding card, not a crash. Building the playlist even during a takeover (second to last) is what lets the screen restore instantly when the takeover clears, without waiting for a poll.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter ManifestBuilderTests`
Expected: FAIL to compile — `ManifestBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/EventPhotoBot/Web/ManifestBuilder.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record ManifestImage(
    string Id, int Width, int Height, string? Caption, string? SenderName, bool Recurring);

public sealed record TakeoverView(string Id, DateTimeOffset? Until);

public sealed record SettingsView(
    int SlideSeconds, int TransitionMs, bool NewestFirstBoost, string Order);

public sealed record Manifest(
    long Version,
    IReadOnlyList<ManifestImage> Images,
    TakeoverView? Takeover,
    SettingsView Settings,
    int PendingCount);

/// <summary>
/// Pure function from state to what the screen should show. No I/O, no clock of
/// its own — the caller supplies 'now' so the behaviour is testable.
/// </summary>
public static class ManifestBuilder
{
    public static Manifest Build(EventState state, long generation, DateTimeOffset now)
    {
        var settings = state.Settings;

        var approved = state.Images.Values
            .Where(i => i.Status == ImageStatus.Approved)
            .ToList();

        var recurring = approved
            .Where(i => i.Pin == PinKind.Recurring)
            .OrderBy(i => i.SortKey, StringComparer.Ordinal)
            .ToList();

        var ordinary = approved.Where(i => i.Pin != PinKind.Recurring).ToList();
        ordinary = settings.Order == SlideOrder.NewestFirst
            ? [.. ordinary.OrderByDescending(i => i.SortKey, StringComparer.Ordinal)]
            : Shuffle(ordinary, generation);

        var playlist = Interleave(ordinary, recurring, Math.Max(1, settings.RecurringEvery));

        return new Manifest(
            Version: generation,
            Images: [.. playlist.Select(ToManifestImage)],
            Takeover: ActiveTakeover(state, now),
            Settings: new SettingsView(
                settings.SlideSeconds, settings.TransitionMs, settings.NewestFirstBoost,
                settings.Order == SlideOrder.NewestFirst ? "newest-first" : "shuffle"),
            PendingCount: state.Images.Values.Count(i => i.Status == ImageStatus.Pending));
    }

    private static ManifestImage ToManifestImage(ImageRecord i) =>
        new(i.Id, i.Width, i.Height, i.Caption, i.SenderName, i.Pin == PinKind.Recurring);

    /// <summary>
    /// Seeded with the generation so the order is stable across polls and only
    /// changes when the state does. An unseeded shuffle would reorder the screen
    /// on every poll that misses the ETag.
    /// </summary>
    private static List<ImageRecord> Shuffle(List<ImageRecord> images, long generation)
    {
        var ordered = images.OrderBy(i => i.SortKey, StringComparer.Ordinal).ToList();
        var random = new Random(unchecked((int)generation));
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }
        return ordered;
    }

    /// <summary>
    /// Drops one recurring image into the playlist after every 'every' ordinary
    /// images, rotating when several hold the pin.
    /// </summary>
    private static List<ImageRecord> Interleave(
        List<ImageRecord> ordinary, List<ImageRecord> recurring, int every)
    {
        if (recurring.Count == 0) return ordinary;
        if (ordinary.Count == 0) return recurring;

        var result = new List<ImageRecord>(ordinary.Count + ordinary.Count / every + 1);
        var next = 0;
        for (var i = 0; i < ordinary.Count; i++)
        {
            result.Add(ordinary[i]);
            if ((i + 1) % every == 0)
            {
                result.Add(recurring[next % recurring.Count]);
                next++;
            }
        }
        if (next == 0) result.Add(recurring[0]);
        return result;
    }

    private static TakeoverView? ActiveTakeover(EventState state, DateTimeOffset now)
    {
        var settings = state.Settings;
        if (settings.TakeoverImageId is not { } id) return null;
        if (!state.Images.ContainsKey(id)) return null;
        if (settings.TakeoverUntil is { } until && until <= now) return null;
        return new TakeoverView(id, settings.TakeoverUntil);
    }
}
```

`if (next == 0) result.Add(recurring[0])` covers the case where there are fewer ordinary images than `recurringEvery`: without it, a programme image pinned before the event would never appear until ten guest photos existed, which is exactly the moment you most want it on screen.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter ManifestBuilderTests`
Expected: PASS, all fourteen.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/Web/ManifestBuilder.cs tests/EventPhotoBot.Tests/ManifestBuilderTests.cs
git commit -m "feat: manifest builder with seeded shuffle, recurring interleave and takeover"
```

---

### Task 8: Image pipeline

**Read the licensing flag near the top of this plan before starting.**

**Files:**
- Create: `src/EventPhotoBot/Imaging/ImagePipeline.cs`
- Create: `tests/EventPhotoBot.Tests/ImagePipelineTests.cs`
- Create: `tests/EventPhotoBot.Tests/TestAssets/` (three generated fixtures)
- Modify: `src/EventPhotoBot/EventPhotoBot.csproj`, `tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record ProcessedImage(byte[] Display, byte[] Thumb, int Width, int Height, string Sha256)`
  - `static ProcessedImage Process(byte[] original)`
  - `static bool IsSupportedMimeType(string? mimeType)`
  - `ImagePipeline.DisplayMaxEdge` = 2560, `ThumbMaxEdge` = 480, `JpegQuality` = 82

- [ ] **Step 1: Add the package to both projects**

```bash
dotnet add src/EventPhotoBot/EventPhotoBot.csproj package SixLabors.ImageSharp
dotnet add tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj package SixLabors.ImageSharp
```

- [ ] **Step 2: Generate the test fixtures**

The orientation test needs a real portrait JPEG carrying EXIF orientation 6 (rotate 90° clockwise), which is what an iPhone produces. Generate it rather than committing a binary of unknown provenance.

Create `tests/EventPhotoBot.Tests/TestAssets/Generate.csx` — or simply run this once as a throwaway console snippet and commit the outputs:

```csharp
// Run with: dotnet run, or paste into a scratch console project referencing ImageSharp.
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

Directory.CreateDirectory("TestAssets");

// A 400x800 portrait image stored as 800x400 landscape with orientation 6,
// which is how a phone records a portrait photo.
using (var image = new Image<Rgb24>(800, 400))
{
    image.Mutate(c => c.Fill(Color.CornflowerBlue));
    // Paint a stripe down what should be the LEFT edge after orientation is applied.
    image.Mutate(c => c.Fill(Color.Red, new RectangleF(0, 0, 800, 40)));
    image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
    image.Metadata.ExifProfile.SetValue(
        SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
    image.Metadata.ExifProfile.SetValue(
        SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.GPSLatitudeRef, "N");
    image.SaveAsJpeg("TestAssets/portrait-exif-6.jpg", new JpegEncoder { Quality = 90 });
}

using (var image = new Image<Rgb24>(4000, 3000))
{
    image.Mutate(c => c.Fill(Color.SeaGreen));
    image.SaveAsJpeg("TestAssets/landscape.jpg", new JpegEncoder { Quality = 90 });
}

using (var image = new Image<Rgba32>(64, 64))
{
    image.Mutate(c => c.Fill(Color.Goldenrod));
    image.SaveAsPng("TestAssets/tiny.png");
}
```

Add to `tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj` inside the top-level `<Project>` element:

```xml
<ItemGroup>
  <None Include="TestAssets\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 3: Write the failing tests**

Create `tests/EventPhotoBot.Tests/ImagePipelineTests.cs`:

```csharp
using EventPhotoBot.Imaging;
using SixLabors.ImageSharp;

namespace EventPhotoBot.Tests;

public class ImagePipelineTests
{
    private static byte[] Asset(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", name));

    [Fact]
    public void Exif_orientation_is_applied_so_portrait_photos_come_out_upright()
    {
        var result = ImagePipeline.Process(Asset("portrait-exif-6.jpg"));

        using var display = Image.Load(result.Display);
        Assert.True(display.Height > display.Width,
            "An orientation-6 source is stored landscape and must come out portrait.");
        Assert.Equal(display.Width, result.Width);
        Assert.Equal(display.Height, result.Height);
    }

    [Fact]
    public void Derivatives_carry_no_exif_metadata()
    {
        var result = ImagePipeline.Process(Asset("portrait-exif-6.jpg"));

        using var display = Image.Load(result.Display);
        using var thumb = Image.Load(result.Thumb);
        Assert.Null(display.Metadata.ExifProfile);
        Assert.Null(thumb.Metadata.ExifProfile);
    }

    [Fact]
    public void The_display_derivative_is_capped_at_the_long_edge()
    {
        var result = ImagePipeline.Process(Asset("landscape.jpg"));

        using var display = Image.Load(result.Display);
        Assert.Equal(ImagePipeline.DisplayMaxEdge, Math.Max(display.Width, display.Height));
    }

    [Fact]
    public void The_thumbnail_is_capped_at_the_thumb_edge()
    {
        var result = ImagePipeline.Process(Asset("landscape.jpg"));

        using var thumb = Image.Load(result.Thumb);
        Assert.Equal(ImagePipeline.ThumbMaxEdge, Math.Max(thumb.Width, thumb.Height));
    }

    [Fact]
    public void Images_smaller_than_the_cap_are_not_upscaled()
    {
        var result = ImagePipeline.Process(Asset("tiny.png"));

        using var display = Image.Load(result.Display);
        Assert.Equal(64, display.Width);
        Assert.Equal(64, display.Height);
    }

    [Fact]
    public void Png_input_produces_jpeg_derivatives()
    {
        var result = ImagePipeline.Process(Asset("tiny.png"));

        var format = Image.DetectFormat(result.Display);
        Assert.Equal("JPEG", format.Name);
    }

    [Fact]
    public void The_hash_is_stable_for_identical_input_and_differs_for_different_input()
    {
        var one = ImagePipeline.Process(Asset("landscape.jpg"));
        var same = ImagePipeline.Process(Asset("landscape.jpg"));
        var other = ImagePipeline.Process(Asset("tiny.png"));

        Assert.Equal(one.Sha256, same.Sha256);
        Assert.NotEqual(one.Sha256, other.Sha256);
        Assert.Equal(64, one.Sha256.Length);
    }

    [Fact]
    public void Corrupt_input_throws_a_clear_exception_rather_than_producing_garbage()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.ThrowsAny<Exception>(() => ImagePipeline.Process(garbage));
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", false)]
    [InlineData("image/gif", false)]
    [InlineData("video/mp4", false)]
    [InlineData(null, false)]
    public void Supported_mime_types_exclude_heic_and_everything_non_photographic(
        string? mimeType, bool supported)
    {
        Assert.Equal(supported, ImagePipeline.IsSupportedMimeType(mimeType));
    }
}
```

HEIC is excluded deliberately: ImageSharp has no HEIC decoder, and the spec's choice is to decline it with a clear message rather than add a native dependency.

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter ImagePipelineTests`
Expected: FAIL to compile — `ImagePipeline` does not exist.

- [ ] **Step 5: Write the implementation**

Create `src/EventPhotoBot/Imaging/ImagePipeline.cs`:

```csharp
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EventPhotoBot.Imaging;

public sealed record ProcessedImage(
    byte[] Display, byte[] Thumb, int Width, int Height, string Sha256);

public static class ImagePipeline
{
    public const int DisplayMaxEdge = 2560;
    public const int ThumbMaxEdge = 480;
    public const int JpegQuality = 82;

    private static readonly HashSet<string> Supported =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg", "image/png", "image/webp" };

    public static bool IsSupportedMimeType(string? mimeType) =>
        mimeType is not null && Supported.Contains(mimeType);

    /// <summary>
    /// Decode, apply EXIF orientation, strip metadata, produce both derivatives.
    /// Orientation is applied before resizing: a portrait photo landing sideways
    /// on the projector is the single most common visible bug in this system.
    /// </summary>
    public static ProcessedImage Process(byte[] original)
    {
        using var image = Image.Load(original);

        image.Mutate(c => c.AutoOrient());

        // Strip everything that could carry GPS coordinates or device identifiers.
        // The display copy is the one that gets served around.
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IccProfile = null;

        var display = Encode(image, DisplayMaxEdge);
        var thumb = Encode(image, ThumbMaxEdge);

        var (width, height) = Scaled(image.Width, image.Height, DisplayMaxEdge);

        return new ProcessedImage(
            display, thumb, width, height, Convert.ToHexStringLower(SHA256.HashData(display)));
    }

    private static byte[] Encode(Image source, int maxEdge)
    {
        var (width, height) = Scaled(source.Width, source.Height, maxEdge);

        using var resized = source.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3,
        }));

        using var buffer = new MemoryStream();
        resized.Save(buffer, new JpegEncoder { Quality = JpegQuality });
        return buffer.ToArray();
    }

    /// <summary>Scales down to fit the long edge. Never scales up.</summary>
    private static (int Width, int Height) Scaled(int width, int height, int maxEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxEdge) return (width, height);

        var factor = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * factor)),
                Math.Max(1, (int)Math.Round(height * factor)));
    }
}
```

The hash is taken over the display derivative rather than the source bytes, which is what the spec means by "hash of the normalised image": the same photo re-sent after a round trip through a messaging app produces different source bytes but the same normalised output, so this catches duplicates the `fileUniqueId` guard misses.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests --filter ImagePipelineTests`
Expected: PASS, all fifteen cases.

- [ ] **Step 7: Commit**

```bash
git add src/EventPhotoBot/Imaging tests/EventPhotoBot.Tests/ImagePipelineTests.cs tests/EventPhotoBot.Tests/TestAssets tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj src/EventPhotoBot/EventPhotoBot.csproj
git commit -m "feat: image pipeline with EXIF orientation, metadata stripping and derivatives"
```

---

### Task 9: Manifest endpoint, image serving, and wiring it all into Program

This task turns the pieces into a running app. It is the first point where `Program.cs` grows past a health check.

**Files:**
- Create: `src/EventPhotoBot/Web/ApiEndpoints.cs`
- Create: `src/EventPhotoBot/Web/ImageEndpoints.cs`
- Modify: `src/EventPhotoBot/Program.cs` (full replacement)
- Create: `tests/EventPhotoBot.Tests/AppFactory.cs`
- Create: `tests/EventPhotoBot.Tests/ManifestEndpointTests.cs`
- Create: `tests/EventPhotoBot.Tests/EndpointAuthTests.cs`

**Interfaces:**
- Consumes: `StateStore`, `IObjectStore`, `AppConfig`, `ManifestBuilder`, `SessionCookie`, `AuthEndpoints`.
- Produces:
  - `static void MapApi(this WebApplication app)` — currently only `GET /api/manifest`
  - `static void MapImages(this WebApplication app)` — `GET /img/{id}/display`, `GET /img/{id}/thumb`
  - `ObjectPaths.Original(string id, string ext)`, `.Display(string id)`, `.Thumb(string id)` in `EventPhotoBot.State`
  - Test helper `AppFactory` exposing `Objects` (the `InMemoryObjectStore`) and `Store` (the `StateStore`), plus `CreateAuthenticatedClient()`

- [ ] **Step 1: Add the object path helper**

Append to `src/EventPhotoBot/State/Models.cs`:

```csharp
/// <summary>The only place object names are constructed.</summary>
public static class ObjectPaths
{
    public static string Original(string id, string extension) => $"originals/{id}.{extension}";
    public static string Display(string id) => $"display/{id}.jpg";
    public static string Thumb(string id) => $"thumbs/{id}.jpg";
}
```

- [ ] **Step 2: Write the test host**

Create `tests/EventPhotoBot.Tests/AppFactory.cs`:

```csharp
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;
using EventPhotoBot.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EventPhotoBot.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Password = "hunter2";
    public const string SigningKey = "0123456789abcdef0123456789abcdef";
    public const string WebhookPath = "hook-abc";
    public const string WebhookSecret = "tg-secret";

    public InMemoryObjectStore Objects { get; } = new();
    public FakeTelegramClient Telegram { get; } = new();

    public StateStore Store => Services.GetRequiredService<StateStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("BUCKET_NAME", "test-bucket");
        builder.UseSetting("EVENT_NAME", "Test Event");
        builder.UseSetting("TELEGRAM_BOT_TOKEN", "test-token");
        builder.UseSetting("TELEGRAM_WEBHOOK_SECRET", WebhookSecret);
        builder.UseSetting("TELEGRAM_WEBHOOK_PATH", WebhookPath);
        builder.UseSetting("ADMIN_PASSWORD", Password);
        builder.UseSetting("COOKIE_SIGNING_KEY", SigningKey);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(Objects);
            services.RemoveAll<ITelegramClient>();
            services.AddSingleton<ITelegramClient>(Telegram);
        });
    }

    /// <summary>A client carrying a valid session cookie, for everything behind the gate.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var cookie = SessionCookie.Issue(SigningKey, DateTimeOffset.UtcNow.AddDays(1));
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}={cookie}");
        return client;
    }

    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
```

`RemoveAll` needs `using Microsoft.Extensions.DependencyInjection.Extensions;` — add it if the compiler asks. The `https` base address matters: the session cookie is `Secure`, so an `http` test client would silently drop it.

- [ ] **Step 3: Write the failing tests**

Create `tests/EventPhotoBot.Tests/EndpointAuthTests.cs`:

```csharp
using System.Net;

namespace EventPhotoBot.Tests;

public class EndpointAuthTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public EndpointAuthTests(AppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/manifest")]
    [InlineData("/img/anything/display")]
    [InlineData("/img/anything/thumb")]
    public async Task Api_and_image_paths_return_401_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/show")]
    [InlineData("/admin/queue")]
    [InlineData("/admin/images")]
    [InlineData("/admin/settings")]
    public async Task Pages_redirect_to_login_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/login")]
    public async Task Open_paths_are_reachable_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_does_not_set_a_session_cookie()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.PostAsync("/login",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("password", "wrong")]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie").DefaultIfEmpty(""),
            v => v.Contains("eventphoto_session="));
    }

    [Fact]
    public async Task The_right_password_sets_a_session_cookie()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.PostAsync("/login",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("password", AppFactory.Password)]));

        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.Contains("eventphoto_session="));
        Assert.Contains(cookies, c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
    }
}
```

Create `tests/EventPhotoBot.Tests/ManifestEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ManifestEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public ManifestEndpointTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_returns_an_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task A_matching_if_none_match_returns_304_with_no_body()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await client.GetAsync("/api/manifest");
        var etag = first.Headers.ETag!.ToString();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(0, (await second.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task A_poll_performs_no_object_store_io()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await client.GetAsync("/api/manifest");
        var etag = first.Headers.ETag!.ToString();

        var readsBefore = _factory.Objects.ReadCount;
        var writesBefore = _factory.Objects.WriteCount;

        for (var i = 0; i < 20; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest");
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            await client.SendAsync(request);
        }

        Assert.Equal(readsBefore, _factory.Objects.ReadCount);
        Assert.Equal(writesBefore, _factory.Objects.WriteCount);
    }

    [Fact]
    public async Task Changing_state_changes_the_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var before = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();

        await _factory.Store.MutateAsync(s => s.Settings.SlideSeconds = 11);

        var after = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Image_bytes_are_served_from_the_object_store()
    {
        await _factory.Objects.WriteAsync(
            ObjectPaths.Display("img1"), [1, 2, 3, 4], "image/jpeg", null);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/img1/display");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_image_returns_404()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/nope/display");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Image_responses_carry_an_immutable_cache_header()
    {
        await _factory.Objects.WriteAsync(
            ObjectPaths.Display("img2"), [9], "image/jpeg", null);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/img2/display");

        var cacheControl = response.Headers.CacheControl!;
        Assert.True(cacheControl.Private);
        Assert.True(cacheControl.MaxAge > TimeSpan.FromDays(1));
    }
}
```

`A_poll_performs_no_object_store_io` is the executable form of the spec's central cost constraint. If a later change makes the manifest handler read state, this test fails.

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter "ManifestEndpointTests|EndpointAuthTests"`
Expected: FAIL — endpoints do not exist, and `AppFactory` will not compile until `FakeTelegramClient` exists. Create a stub now and fill it in Task 10:

```csharp
// tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs — stub for now, completed in Task 10.
using EventPhotoBot.Telegram;

namespace EventPhotoBot.Tests.Fakes;

public sealed class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, string Text)> Sent { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default) =>
        Task.FromResult($"path/{fileId}");

    public Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default) =>
        Files.TryGetValue(filePath, out var bytes)
            ? Task.FromResult(bytes)
            : throw new InvalidOperationException($"No fake file at '{filePath}'.");

    public Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        Sent.Add((chatId, text));
        return Task.CompletedTask;
    }
}
```

And the interface it implements, `src/EventPhotoBot/Telegram/ITelegramClient.cs`:

```csharp
namespace EventPhotoBot.Telegram;

public interface ITelegramClient
{
    Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default);
    Task SendMessageAsync(long chatId, string text, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the endpoints**

Create `src/EventPhotoBot/Web/ApiEndpoints.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/manifest", (HttpContext http, StateStore store) =>
        {
            // Served entirely from memory. No object-store I/O on this path, ever:
            // it runs every two seconds per open page for the length of the event.
            var etag = $"\"{store.Generation}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";

            return Results.Ok(ManifestBuilder.Build(
                store.Snapshot, store.Generation, DateTimeOffset.UtcNow));
        });
    }
}
```

Create `src/EventPhotoBot/Web/ImageEndpoints.cs`:

```csharp
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public static class ImageEndpoints
{
    public static void MapImages(this WebApplication app)
    {
        app.MapGet("/img/{id}/display", (string id, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Display(id), objects, ct));

        app.MapGet("/img/{id}/thumb", (string id, IObjectStore objects, CancellationToken ct) =>
            Serve(ObjectPaths.Thumb(id), objects, ct));
    }

    /// <summary>
    /// Bytes are proxied rather than served from public bucket URLs so the password
    /// gate covers them. A given id's bytes never change, so they cache hard —
    /// which is what keeps this off the hot path despite the proxying.
    /// </summary>
    private static async Task<IResult> Serve(string path, IObjectStore objects, CancellationToken ct)
    {
        var stream = await objects.OpenReadAsync(path, ct);
        if (stream is null) return Results.NotFound();

        return Results.Stream(stream, "image/jpeg", enableRangeProcessing: false);
    }
}
```

The `Cache-Control` header is set in middleware rather than per-result so both routes get it without repetition — added in the next step.

- [ ] **Step 6: Rewrite Program.cs**

Replace `src/EventPhotoBot/Program.cs` entirely:

```csharp
using EventPhotoBot;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Web;
using Google.Cloud.Storage.V1;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var config = AppConfig.Load(builder.Configuration);
builder.Services.AddSingleton(config);

builder.Services.AddSingleton<IObjectStore>(sp => new GcsObjectStore(
    config.BucketName,
    StorageClient.Create(),
    sp.GetRequiredService<ILogger<GcsObjectStore>>()));

builder.Services.AddSingleton<StateStore>();
builder.Services.AddHttpClient<ITelegramClient, TelegramClient>();
builder.Services.AddLoginRateLimiter();

var app = builder.Build();

config.LogLoaded(app.Logger);

// Load state once, at startup. This is the only read of state.json.
await app.Services.GetRequiredService<StateStore>().LoadAsync();

app.UseRateLimiter();

// Long, private cache lifetimes on image bytes: a given id's bytes never change.
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/img"))
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    await next();
});

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapAuth(config);
app.UseSessionGate(config);

app.UseStaticFiles();
app.MapApi();
app.MapImages();

app.MapGet("/show", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "show.html"), "text/html"));
app.MapGet("/admin/queue", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "queue.html"), "text/html"));
app.MapGet("/admin/images", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "images.html"), "text/html"));
app.MapGet("/admin/settings", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin", "settings.html"), "text/html"));
app.MapGet("/", () => Results.Redirect("/show"));

app.Run();

public partial class Program { }
```

Order matters here in a way that is easy to get wrong: `MapAuth` registers `/login` before `UseSessionGate` runs, and the gate's open-path set includes `/login` — both are needed, because the gate runs for every request regardless of which endpoint matched. `UseStaticFiles` sits *after* the gate so static assets are covered by it too.

`StorageClient.Create()` uses Application Default Credentials, which on Cloud Run is the service account attached to the revision. Nothing to configure.

- [ ] **Step 7: Create placeholder pages so the route tests pass**

The auth tests hit `/show` and the three admin routes. Create minimal placeholders now; Tasks 12 and 13 replace them.

```bash
mkdir -p src/EventPhotoBot/wwwroot/admin
for f in show admin/queue admin/images admin/settings; do
  printf '<!doctype html><meta charset="utf-8"><title>placeholder</title><p>placeholder\n' \
    > "src/EventPhotoBot/wwwroot/$f.html"
done
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests`
Expected: PASS, everything green including the earlier tasks' tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: manifest endpoint with ETag, image proxying and application wiring"
```

---

### Task 10: Telegram ingest

**Files:**
- Create: `src/EventPhotoBot/Telegram/TelegramModels.cs`, `src/EventPhotoBot/Telegram/TelegramClient.cs`, `src/EventPhotoBot/Telegram/UpdateHandler.cs`
- Modify: `src/EventPhotoBot/Program.cs` (add the webhook route and register `UpdateHandler`)
- Modify: `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs` (fill in the stub)
- Create: `tests/EventPhotoBot.Tests/UpdateHandlerTests.cs`
- Modify: `src/EventPhotoBot/EventPhotoBot.csproj` (add `Ulid`)

**Interfaces:**
- Consumes: `ITelegramClient` (Task 9), `StateStore`, `IObjectStore`, `ImagePipeline`, `ObjectPaths`, `AppConfig`.
- Produces:
  - `UpdateHandler(StateStore store, IObjectStore objects, ITelegramClient telegram, ILogger<UpdateHandler> logger)`
  - `Task HandleAsync(TgUpdate update, CancellationToken ct = default)`
  - DTOs `TgUpdate`, `TgMessage`, `TgUser`, `TgPhotoSize`, `TgDocument`, `TgChat`

- [ ] **Step 1: Add the ULID package**

```bash
dotnet add src/EventPhotoBot/EventPhotoBot.csproj package Ulid
```

ULIDs sort lexically by creation time, which is what makes `sortKey` default to received order with no extra field and no index.

- [ ] **Step 2: Write the Telegram DTOs**

Create `src/EventPhotoBot/Telegram/TelegramModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace EventPhotoBot.Telegram;

public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("from")] public TgUser? From { get; set; }
    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("media_group_id")] public string? MediaGroupId { get; set; }
    [JsonPropertyName("photo")] public List<TgPhotoSize>? Photo { get; set; }
    [JsonPropertyName("document")] public TgDocument? Document { get; set; }
}

public sealed class TgUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName!
        : !string.IsNullOrWhiteSpace(Username) ? $"@{Username}"
        : Id.ToString();
}

public sealed class TgChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class TgPhotoSize
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class TgDocument
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
}
```

- [ ] **Step 3: Write the HTTP client**

Create `src/EventPhotoBot/Telegram/TelegramClient.cs`:

```csharp
using System.Text.Json;

namespace EventPhotoBot.Telegram;

public sealed class TelegramClient(HttpClient http, AppConfig config) : ITelegramClient
{
    private string Api => $"https://api.telegram.org/bot{config.BotToken}";

    public async Task<string> GetFilePathAsync(string fileId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/getFile?file_id={Uri.EscapeDataString(fileId)}", ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.GetProperty("result").GetProperty("file_path").GetString()
               ?? throw new InvalidOperationException("getFile returned no file_path.");
    }

    public async Task<byte[]> DownloadAsync(string filePath, CancellationToken ct = default) =>
        await http.GetByteArrayAsync(
            $"https://api.telegram.org/file/bot{config.BotToken}/{filePath}", ct);

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Api}/sendMessage", new { chat_id = chatId, text }, ct);
        // A failed reply must not fail the ingest that already succeeded.
        if (!response.IsSuccessStatusCode)
            Console.Error.WriteLine($"sendMessage failed: {(int)response.StatusCode}");
    }
}
```

`SendMessageAsync` swallows failures deliberately. The photo is already stored by the time the reply is sent; losing the acknowledgement is a worse outcome than losing the photo only if you let the exception propagate.

- [ ] **Step 4: Write the failing tests**

Create `tests/EventPhotoBot.Tests/UpdateHandlerTests.cs`:

```csharp
using EventPhotoBot.Imaging;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class UpdateHandlerTests
{
    private const long Guest = 111;
    private const long Stranger = 999;

    private static byte[] SamplePhoto() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

    private sealed class Harness
    {
        public InMemoryObjectStore Objects { get; } = new();
        public FakeTelegramClient Telegram { get; } = new();
        public StateStore Store { get; private set; } = null!;
        public UpdateHandler Handler { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Action<Settings>? configure = null)
        {
            var harness = new Harness();
            harness.Store = new StateStore(harness.Objects);
            await harness.Store.LoadAsync();
            if (configure is not null)
                await harness.Store.MutateAsync(s => configure(s.Settings));
            harness.Handler = new UpdateHandler(
                harness.Store, harness.Objects, harness.Telegram,
                NullLogger<UpdateHandler>.Instance);
            return harness;
        }
    }

    private static TgUpdate PhotoFrom(long userId, string fileUniqueId = "u1",
        string? mediaGroupId = null, string? caption = null) => new()
    {
        UpdateId = 1,
        Message = new TgMessage
        {
            From = new TgUser { Id = userId, FirstName = "Guest" },
            Chat = new TgChat { Id = userId },
            MediaGroupId = mediaGroupId,
            Caption = caption,
            Photo =
            [
                new TgPhotoSize { FileId = "small", FileUniqueId = fileUniqueId, Width = 90, Height = 60 },
                new TgPhotoSize { FileId = "large", FileUniqueId = fileUniqueId, Width = 1600, Height = 1200 },
            ],
        },
    };

    private static void StockFile(Harness harness, string fileId) =>
        harness.Telegram.Files[$"path/{fileId}"] = SamplePhoto();

    [Fact]
    public async Task A_whitelisted_sender_gets_their_photo_queued()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Pending, image.Status);
        Assert.Equal(Guest, image.SenderId);
        Assert.Equal(ImageSource.Telegram, image.Source);
    }

    [Fact]
    public async Task The_largest_photo_size_is_the_one_downloaded()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");
        // "small" is deliberately not stocked: using it would throw.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task All_three_objects_are_written_before_the_state_entry_exists()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var id = harness.Store.Snapshot.Images.Keys.Single();
        Assert.Contains(ObjectPaths.Display(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Thumb(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Original(id, "jpg"), harness.Objects.Paths);
    }

    [Fact]
    public async Task A_trusted_sender_skips_the_queue_when_auto_approve_is_on()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AutoApproveTrusted = true;
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest", Trusted = true });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Approved, harness.Store.Snapshot.Images.Values.Single().Status);
    }

    [Fact]
    public async Task A_trusted_sender_still_queues_when_auto_approve_is_off()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AutoApproveTrusted = false;
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest", Trusted = true });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Pending, harness.Store.Snapshot.Images.Values.Single().Status);
    }

    [Fact]
    public async Task An_unlisted_sender_is_declined_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Objects.Paths.Where(p => p.StartsWith("display/")));
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Pairing_mode_replies_with_the_id_and_records_the_sender_without_storing_photos()
    {
        var harness = await Harness.CreateAsync(s => s.PairingMode = true);
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        var seen = Assert.Single(harness.Store.Snapshot.Settings.SeenSenders);
        Assert.Equal(Stranger, seen.Id);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains(Stranger.ToString()));
    }

    [Fact]
    public async Task Pairing_mode_records_a_sender_only_once()
    {
        var harness = await Harness.CreateAsync(s => s.PairingMode = true);
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));
        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Single(harness.Store.Snapshot.Settings.SeenSenders);
    }

    [Fact]
    public async Task A_duplicate_file_unique_id_is_rejected_without_a_second_copy()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Single(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_identical_image_sent_with_a_different_file_id_is_caught_by_the_hash()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "first"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "second"));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task An_album_gets_one_acknowledgement_rather_than_one_per_photo()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        for (var i = 0; i < 5; i++)
        {
            harness.Telegram.Files[$"path/large{i}"] = SamplePhoto();
            var update = PhotoFrom(Guest, fileUniqueId: $"album{i}", mediaGroupId: "group-1");
            update.Message!.Photo![1].FileId = $"large{i}";
            await harness.Handler.HandleAsync(update);
        }

        Assert.Equal(5, harness.Store.Snapshot.Images.Count);
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_caption_is_stored_with_the_image()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, caption: "Cake time"));

        Assert.Equal("Cake time", harness.Store.Snapshot.Images.Values.Single().Caption);
    }

    [Fact]
    public async Task A_non_photo_message_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Text = "hello",
            },
        });

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("photo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_heic_document_is_declined_with_a_clear_message()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Document = new TgDocument
                {
                    FileId = "doc", FileUniqueId = "doc1",
                    MimeType = "image/heic", FileName = "IMG_0001.HEIC",
                },
            },
        });

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("HEIC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_file_over_the_twenty_megabyte_bot_limit_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        var update = PhotoFrom(Guest);
        update.Message!.Photo![1].FileSize = 25 * 1024 * 1024;

        await harness.Handler.HandleAsync(update);

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_download_failure_apologises_and_stores_nothing()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        // No file stocked, so DownloadAsync throws.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("sorry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Start_tells_an_unlisted_sender_they_are_not_on_the_list()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Stranger, FirstName = "Nobody" },
                Chat = new TgChat { Id = Stranger },
                Text = "/start",
            },
        });

        var reply = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("not on the list", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_tells_a_listed_sender_what_happens_to_their_photos()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Text = "/start",
            },
        });

        var reply = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("deleted", reply.Text, StringComparison.OrdinalIgnoreCase);
    }
}
```

The `/start` privacy test is the executable form of the spec's privacy note: senders are told photos go on a screen and are deleted afterwards. It is a requirement, not a nicety, so it gets a test.

- [ ] **Step 5: Complete the fake**

Replace `tests/EventPhotoBot.Tests/Fakes/FakeTelegramClient.cs` with the version already given in Task 9 Step 4 — it is complete as written. No change needed.

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter UpdateHandlerTests`
Expected: FAIL to compile — `UpdateHandler` does not exist.

- [ ] **Step 7: Write the update handler**

Create `src/EventPhotoBot/Telegram/UpdateHandler.cs`:

```csharp
using EventPhotoBot.Imaging;
using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public sealed class UpdateHandler(
    StateStore store,
    IObjectStore objects,
    ITelegramClient telegram,
    ILogger<UpdateHandler> logger)
{
    private const long MaxDownloadBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Album sends arrive as separate updates sharing a media_group_id. Each is its
    /// own image; the group exists only so a five-photo album gets one reply.
    /// In-memory and unbounded, which is fine: one instance, one event.
    /// </summary>
    private readonly HashSet<string> _acknowledgedGroups = [];

    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        var message = update.Message;
        if (message?.From is not { } sender || message.Chat is not { } chat) return;

        var settings = store.Snapshot.Settings;
        var entry = settings.Whitelist.FirstOrDefault(w => w.Id == sender.Id);

        if (entry is null)
        {
            await HandleUnlistedAsync(sender, chat, settings.PairingMode, ct);
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

    private async Task HandleUnlistedAsync(
        TgUser sender, TgChat chat, bool pairingMode, CancellationToken ct)
    {
        if (!pairingMode)
        {
            await telegram.SendMessageAsync(chat.Id,
                "You are not on the list for this event, so I cannot accept photos from you.", ct);
            return;
        }

        await store.MutateAsync(state =>
        {
            if (state.Settings.SeenSenders.Any(s => s.Id == sender.Id)) return;
            state.Settings.SeenSenders.Add(new SeenSender
            {
                Id = sender.Id,
                Name = sender.DisplayName,
                FirstSeen = DateTimeOffset.UtcNow,
            });
        });

        await telegram.SendMessageAsync(chat.Id,
            $"Your Telegram id is {sender.Id}. An organiser can add you to the list now — " +
            "try again once they have.", ct);
    }

    private sealed record Candidate(
        string FileId, string FileUniqueId, string Extension, string? DeclineReason);

    /// <summary>
    /// Photo messages carry several sizes; take the largest. Documents are accepted
    /// only for the formats the pipeline can actually decode.
    /// </summary>
    private static Candidate? Extract(TgMessage message)
    {
        if (message.Photo is { Count: > 0 } photos)
        {
            var largest = photos.MaxBy(p => (long)p.Width * p.Height)!;
            return new Candidate(largest.FileId, largest.FileUniqueId, "jpg",
                largest.FileSize > MaxDownloadBytes
                    ? "That photo is too large for me to fetch — Telegram caps bot downloads at 20 MB."
                    : null);
        }

        if (message.Document is { } document)
        {
            if (string.Equals(document.MimeType, "image/heic", StringComparison.OrdinalIgnoreCase))
                return new Candidate(document.FileId, document.FileUniqueId, "heic",
                    "I cannot read HEIC files. Send it as a photo rather than a file, " +
                    "or convert it to JPEG first.");

            if (!ImagePipeline.IsSupportedMimeType(document.MimeType))
                return null;

            var extension = document.MimeType!.Split('/')[^1].ToLowerInvariant();
            return new Candidate(document.FileId, document.FileUniqueId, extension,
                document.FileSize > MaxDownloadBytes
                    ? "That file is too large for me to fetch — Telegram caps bot downloads at 20 MB."
                    : null);
        }

        return null;
    }

    private async Task IngestAsync(
        TgMessage message, TgUser sender, TgChat chat,
        WhitelistEntry entry, Candidate candidate, CancellationToken ct)
    {
        var snapshot = store.Snapshot;

        if (snapshot.Images.Values.Any(i => i.FileUniqueId == candidate.FileUniqueId))
        {
            await AcknowledgeAsync(message, chat, "I already have that one.", ct);
            return;
        }

        byte[] original;
        try
        {
            // The whole download happens inside the request: CPU is only allocated
            // while a request is in flight, so there is nowhere to defer this to.
            var path = await telegram.GetFilePathAsync(candidate.FileId, ct);
            original = await telegram.DownloadAsync(path, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Download failed for file {FileId}.", candidate.FileId);
            await telegram.SendMessageAsync(chat.Id,
                "Sorry — I could not fetch that photo. Please send it again.", ct);
            return;
        }

        ProcessedImage processed;
        try
        {
            processed = ImagePipeline.Process(original);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not decode an image from {Sender}.", sender.Id);
            await telegram.SendMessageAsync(chat.Id,
                "Sorry — I could not read that image. Please try another.", ct);
            return;
        }

        if (snapshot.Images.Values.Any(i => i.Sha256 == processed.Sha256))
        {
            await AcknowledgeAsync(message, chat, "I already have that one.", ct);
            return;
        }

        var id = Ulid.NewUlid().ToString();
        var approved = entry.Trusted && snapshot.Settings.AutoApproveTrusted;
        var now = DateTimeOffset.UtcNow;

        // Objects first, state last: a failure here leaves orphaned bytes,
        // never a manifest entry pointing at nothing.
        await objects.WriteAsync(ObjectPaths.Original(id, candidate.Extension),
            original, "application/octet-stream", null, ct);
        await objects.WriteAsync(ObjectPaths.Display(id), processed.Display, "image/jpeg", null, ct);
        await objects.WriteAsync(ObjectPaths.Thumb(id), processed.Thumb, "image/jpeg", null, ct);

        await store.MutateAsync(state => state.Images[id] = new ImageRecord
        {
            Id = id,
            Source = ImageSource.Telegram,
            SenderId = sender.Id,
            SenderName = sender.DisplayName,
            FileUniqueId = candidate.FileUniqueId,
            Sha256 = processed.Sha256,
            Caption = string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption,
            Status = approved ? ImageStatus.Approved : ImageStatus.Pending,
            Pin = PinKind.None,
            Width = processed.Width,
            Height = processed.Height,
            ReceivedAt = now,
            DecidedAt = approved ? now : null,
            SortKey = id,
            OriginalExtension = candidate.Extension,
        });

        await AcknowledgeAsync(message, chat,
            approved ? "Got it — it is on the screen now." : "Got it — an organiser will approve it shortly.",
            ct);
    }

    /// <summary>One reply per send, or one per album rather than one per photo.</summary>
    private async Task AcknowledgeAsync(
        TgMessage message, TgChat chat, string text, CancellationToken ct)
    {
        if (message.MediaGroupId is { } group && !_acknowledgedGroups.Add(group)) return;
        await telegram.SendMessageAsync(chat.Id, text, ct);
    }
}
```

`SortKey = id` works because ULIDs sort lexically by creation time — the spec's "defaults to `receivedAt`" without a second time-formatted field to keep in sync.

- [ ] **Step 8: Map the webhook route**

In `src/EventPhotoBot/Program.cs`, register the handler alongside the other services:

```csharp
builder.Services.AddSingleton<UpdateHandler>();
```

And add the route immediately after `app.MapGet("/healthz", ...)`, before `MapAuth`:

```csharp
app.MapPost($"/tg/{config.WebhookPath}",
    async (HttpContext http, TgUpdate update, UpdateHandler handler, CancellationToken ct) =>
    {
        if (http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] != config.WebhookSecret)
            return Results.Unauthorized();

        try
        {
            await handler.HandleAsync(update, ct);
        }
        catch (Exception e)
        {
            app.Logger.LogError(e, "Unhandled error processing update {UpdateId}.", update.UpdateId);
        }

        // Always 200 once the update is parsed. Telegram's retry would resend the
        // whole update and risk duplicates; the sender already got an apology.
        return Results.Ok();
    });
```

The `catch` is deliberately broad. Letting an exception escape returns 500, which makes Telegram retry, which is the one behaviour the spec rules out.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests`
Expected: PASS, all suites.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: Telegram ingest with whitelist, pairing mode, duplicate guards and album replies"
```

---

### Task 11: Admin API — status, pin, takeover, delete, upload, settings

The takeover invariants live here. They are the reason this task exists as its own reviewable unit.

**Files:**
- Modify: `src/EventPhotoBot/Web/ApiEndpoints.cs`
- Create: `tests/EventPhotoBot.Tests/AdminApiTests.cs`
- Create: `tests/EventPhotoBot.Tests/TakeoverInvariantTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–10.
- Produces these routes, all behind the session gate:
  - `POST /api/images/{id}/status` — body `{ "status": "approved" | "hidden" | "rejected" }`
  - `POST /api/images/{id}/pin` — body `{ "pin": "none" | "recurring" }`
  - `PUT /api/takeover` — body `{ "imageId": "...", "minutes": 15 | null }`
  - `DELETE /api/takeover`
  - `DELETE /api/images/{id}`
  - `POST /api/images` — multipart upload, arrives `approved`
  - `PATCH /api/settings` — partial settings including the whitelist and pairing mode
- Produces request DTOs `StatusRequest`, `PinRequest`, `TakeoverRequest`, `SettingsPatch`

- [ ] **Step 1: Write the failing invariant tests**

Create `tests/EventPhotoBot.Tests/TakeoverInvariantTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

/// <summary>
/// Takeover is a settings field rather than a per-image flag precisely so that
/// "only one image holds the screen" is representable. These tests are what keep
/// that true as the delete and hide paths evolve.
/// </summary>
public class TakeoverInvariantTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public TakeoverInvariantTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status = ImageStatus.Approved)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Setting_takeover_on_a_second_image_replaces_the_first()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await SeedImageAsync();
        var second = await SeedImageAsync();

        await client.PutAsJsonAsync("/api/takeover", new { imageId = first, minutes = (int?)null });
        await client.PutAsJsonAsync("/api/takeover", new { imageId = second, minutes = (int?)null });

        Assert.Equal(second, _factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Setting_takeover_on_a_pending_image_approves_it_in_the_same_write()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(ImageStatus.Pending);

        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 5 });

        Assert.Equal(id, _factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Equal(ImageStatus.Approved, _factory.Store.Snapshot.Images[id].Status);
    }

    [Fact]
    public async Task Deleting_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.DeleteAsync($"/api/images/{id}");

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task Hiding_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "hidden" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Rejecting_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "rejected" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Clearing_takeover_nulls_both_fields()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 60 });

        await client.DeleteAsync("/api/takeover");

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task A_duration_sets_an_expiry_and_no_duration_means_until_cleared()
    {
        var client = _factory.CreateAuthenticatedClient();
        var timed = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = timed, minutes = 15 });
        var until = _factory.Store.Snapshot.Settings.TakeoverUntil;

        Assert.NotNull(until);
        Assert.InRange(until!.Value,
            DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        var openEnded = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = openEnded, minutes = (int?)null });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task Takeover_on_an_unknown_image_is_rejected()
    {
        var response = await _factory.CreateAuthenticatedClient()
            .PutAsJsonAsync("/api/takeover", new { imageId = "nope", minutes = (int?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pin_no_longer_accepts_takeover_as_a_value()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/pin", new { pin = "takeover" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

Create `tests/EventPhotoBot.Tests/AdminApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class AdminApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public AdminApiTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status = ImageStatus.Pending)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Approving_an_image_sets_status_and_stamps_the_decision_time()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images[id];
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task An_unknown_status_value_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "banana" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setting_a_recurring_pin_works()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(ImageStatus.Approved);

        await client.PostAsJsonAsync($"/api/images/{id}/pin", new { pin = "recurring" });

        Assert.Equal(PinKind.Recurring, _factory.Store.Snapshot.Images[id].Pin);
    }

    [Fact]
    public async Task Deleting_an_image_removes_the_entry_and_every_object()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        await client.DeleteAsync($"/api/images/{id}");

        Assert.DoesNotContain(id, _factory.Store.Snapshot.Images.Keys);
        Assert.DoesNotContain(ObjectPaths.Display(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Thumb(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Deleting_an_unknown_image_returns_404()
    {
        var response = await _factory.CreateAuthenticatedClient().DeleteAsync("/api/images/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_upload_arrives_approved_with_all_three_objects()
    {
        var client = _factory.CreateAuthenticatedClient();
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "programme.jpg");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images.Values
            .Single(i => i.Source == ImageSource.Admin);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.Null(image.SenderId);
        Assert.Contains(ObjectPaths.Display(image.Id), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Uploading_something_that_is_not_an_image_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "notes.txt");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Settings_can_be_patched_one_field_at_a_time()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 12 });
        await client.PatchAsJsonAsync("/api/settings", new { order = "newest-first" });

        var settings = _factory.Store.Snapshot.Settings;
        Assert.Equal(12, settings.SlideSeconds);
        Assert.Equal(SlideOrder.NewestFirst, settings.Order);
        Assert.Equal(800, settings.TransitionMs); // untouched fields survive
    }

    [Fact]
    public async Task The_whitelist_is_editable_and_takes_effect_without_a_restart()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new
        {
            whitelist = new[] { new { id = 555L, name = "Late Guest", trusted = true } },
        });

        var entry = Assert.Single(_factory.Store.Snapshot.Settings.Whitelist);
        Assert.Equal(555L, entry.Id);
        Assert.True(entry.Trusted);
    }

    [Fact]
    public async Task Pairing_mode_can_be_toggled_and_seen_senders_cleared()
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.Store.MutateAsync(s =>
            s.Settings.SeenSenders.Add(new SeenSender { Id = 1, Name = "Someone" }));

        await client.PatchAsJsonAsync("/api/settings", new { pairingMode = true });
        Assert.True(_factory.Store.Snapshot.Settings.PairingMode);

        await client.PatchAsJsonAsync("/api/settings", new { clearSeenSenders = true });
        Assert.Empty(_factory.Store.Snapshot.Settings.SeenSenders);
    }

    [Fact]
    public async Task A_settings_change_advances_the_manifest_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var before = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 7 });

        var after = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();
        Assert.NotEqual(before, after);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EventPhotoBot.Tests --filter "AdminApiTests|TakeoverInvariantTests"`
Expected: FAIL — routes return 404.

- [ ] **Step 3: Write the endpoints**

Replace `src/EventPhotoBot/Web/ApiEndpoints.cs` entirely:

```csharp
using EventPhotoBot.Imaging;
using EventPhotoBot.State;

namespace EventPhotoBot.Web;

public sealed record StatusRequest(string Status);
public sealed record PinRequest(string Pin);
public sealed record TakeoverRequest(string ImageId, int? Minutes);

public sealed record SettingsPatch(
    int? SlideSeconds,
    int? TransitionMs,
    string? Order,
    bool? NewestFirstBoost,
    int? RecurringEvery,
    bool? AutoApproveTrusted,
    bool? PairingMode,
    List<WhitelistEntry>? Whitelist,
    bool? ClearSeenSenders);

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/manifest", (HttpContext http, StateStore store) =>
        {
            // Served entirely from memory. No object-store I/O on this path, ever:
            // it runs every two seconds per open page for the length of the event.
            var etag = $"\"{store.Generation}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";

            return Results.Ok(ManifestBuilder.Build(
                store.Snapshot, store.Generation, DateTimeOffset.UtcNow));
        });

        app.MapPost("/api/images/{id}/status",
            async (string id, StatusRequest request, StateStore store) =>
            {
                if (!Enum.TryParse<ImageStatus>(request.Status, ignoreCase: true, out var status)
                    || status == ImageStatus.Pending)
                    return Results.BadRequest(
                        new { error = "status must be approved, hidden or rejected." });

                return await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var image)) return Results.NotFound();

                    image.Status = status;
                    image.DecidedAt = DateTimeOffset.UtcNow;

                    // An image that is no longer approved cannot be holding the screen.
                    if (status != ImageStatus.Approved) ClearTakeoverIfHeldBy(state, id);

                    return Results.Ok();
                });
            });

        app.MapPost("/api/images/{id}/pin",
            async (string id, PinRequest request, StateStore store) =>
            {
                // "takeover" is deliberately not a pin value — see PUT /api/takeover.
                if (!Enum.TryParse<PinKind>(request.Pin, ignoreCase: true, out var pin))
                    return Results.BadRequest(new { error = "pin must be none or recurring." });

                return await store.MutateAsync(state =>
                {
                    if (!state.Images.TryGetValue(id, out var image)) return Results.NotFound();
                    image.Pin = pin;
                    return Results.Ok();
                });
            });

        app.MapPut("/api/takeover", async (TakeoverRequest request, StateStore store) =>
            await store.MutateAsync(state =>
            {
                if (!state.Images.TryGetValue(request.ImageId, out var image))
                    return Results.NotFound();

                // Takeover implies the image is on screen, so it is approved by definition.
                if (image.Status != ImageStatus.Approved)
                {
                    image.Status = ImageStatus.Approved;
                    image.DecidedAt = DateTimeOffset.UtcNow;
                }

                state.Settings.TakeoverImageId = request.ImageId;
                state.Settings.TakeoverUntil = request.Minutes is { } minutes
                    ? DateTimeOffset.UtcNow.AddMinutes(minutes)
                    : null;

                return Results.Ok();
            }));

        app.MapDelete("/api/takeover", async (StateStore store) =>
        {
            await store.MutateAsync(state =>
            {
                state.Settings.TakeoverImageId = null;
                state.Settings.TakeoverUntil = null;
            });
            return Results.Ok();
        });

        app.MapDelete("/api/images/{id}",
            async (string id, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                var removed = await store.MutateAsync(state =>
                {
                    if (!state.Images.Remove(id, out var image)) return (ImageRecord?)null;
                    ClearTakeoverIfHeldBy(state, id);
                    return image;
                });

                if (removed is null) return Results.NotFound();

                // State first here, unlike ingest: an entry pointing at deleted bytes
                // would put a broken image on the projector, while orphaned bytes are
                // invisible and the lifecycle rule sweeps them up.
                await objects.DeleteAsync(ObjectPaths.Display(id), ct);
                await objects.DeleteAsync(ObjectPaths.Thumb(id), ct);
                await objects.DeleteAsync(ObjectPaths.Original(id, removed.OriginalExtension), ct);

                return Results.Ok();
            });

        app.MapPost("/api/images",
            async (HttpRequest http, StateStore store, IObjectStore objects, CancellationToken ct) =>
            {
                if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected a file upload." });

                var form = await http.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "No file supplied." });

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var original = buffer.ToArray();

                ProcessedImage processed;
                try
                {
                    processed = ImagePipeline.Process(original);
                }
                catch
                {
                    return Results.BadRequest(new { error = "That file is not an image I can read." });
                }

                var id = Ulid.NewUlid().ToString();
                var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(extension)) extension = "jpg";
                var now = DateTimeOffset.UtcNow;

                await objects.WriteAsync(ObjectPaths.Original(id, extension),
                    original, "application/octet-stream", null, ct);
                await objects.WriteAsync(ObjectPaths.Display(id), processed.Display, "image/jpeg", null, ct);
                await objects.WriteAsync(ObjectPaths.Thumb(id), processed.Thumb, "image/jpeg", null, ct);

                await store.MutateAsync(state => state.Images[id] = new ImageRecord
                {
                    Id = id,
                    Source = ImageSource.Admin,
                    SenderId = null,
                    SenderName = null,
                    FileUniqueId = null,
                    Sha256 = processed.Sha256,
                    Caption = null,
                    Status = ImageStatus.Approved,
                    Pin = PinKind.None,
                    Width = processed.Width,
                    Height = processed.Height,
                    ReceivedAt = now,
                    DecidedAt = now,
                    SortKey = id,
                    OriginalExtension = extension,
                });

                return Results.Ok(new { id });
            }).DisableAntiforgery();

        app.MapPatch("/api/settings", async (SettingsPatch patch, StateStore store) =>
        {
            if (patch.Order is { } order
                && order is not ("shuffle" or "newest-first"))
                return Results.BadRequest(new { error = "order must be shuffle or newest-first." });

            await store.MutateAsync(state =>
            {
                var s = state.Settings;
                if (patch.SlideSeconds is { } slideSeconds) s.SlideSeconds = Math.Clamp(slideSeconds, 2, 120);
                if (patch.TransitionMs is { } transitionMs) s.TransitionMs = Math.Clamp(transitionMs, 0, 5000);
                if (patch.Order is { } o) s.Order = o == "newest-first" ? SlideOrder.NewestFirst : SlideOrder.Shuffle;
                if (patch.NewestFirstBoost is { } boost) s.NewestFirstBoost = boost;
                if (patch.RecurringEvery is { } every) s.RecurringEvery = Math.Clamp(every, 1, 100);
                if (patch.AutoApproveTrusted is { } auto) s.AutoApproveTrusted = auto;
                if (patch.PairingMode is { } pairing) s.PairingMode = pairing;
                if (patch.Whitelist is { } whitelist) s.Whitelist = whitelist;
                if (patch.ClearSeenSenders is true) s.SeenSenders.Clear();
            });

            return Results.Ok();
        });
    }

    private static void ClearTakeoverIfHeldBy(EventState state, string id)
    {
        if (state.Settings.TakeoverImageId != id) return;
        state.Settings.TakeoverImageId = null;
        state.Settings.TakeoverUntil = null;
    }
}
```

Three things here matter and are easy to undo by accident:

`ClearTakeoverIfHeldBy` is called from both the status route and the delete route, inside the same `MutateAsync` as the change that triggers it. Doing it in a second mutation would leave a window where the manifest names a takeover image that is hidden or gone.

The delete route writes state *before* deleting objects, the opposite of ingest. The asymmetry is deliberate and the comment explains it: a manifest entry pointing at deleted bytes is a broken image on the projector, while orphaned bytes are invisible and the lifecycle rule sweeps them.

`Enum.TryParse<PinKind>` rejects `"takeover"` for free, because `PinKind` no longer has that member. The test asserting a 400 is what keeps it that way if someone adds the member back.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EventPhotoBot.Tests`
Expected: PASS, all suites.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: admin API with takeover invariants, upload and editable whitelist"
```

---

### Task 12: Slideshow page

**Files:**
- Replace: `src/EventPhotoBot/wwwroot/show.html`
- Create: `src/EventPhotoBot/wwwroot/show.css`, `src/EventPhotoBot/wwwroot/show.js`

**Interfaces:**
- Consumes: `GET /api/manifest` (Task 9/11), `GET /img/{id}/display` (Task 9).
- Produces: nothing other tasks consume.

No unit tests: this is browser behaviour over an already-tested contract. It is verified by the manual checks in Step 4 and by the acceptance criteria in Task 16.

- [ ] **Step 1: Write the markup**

Replace `src/EventPhotoBot/wwwroot/show.html`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Event photos</title>
  <link rel="stylesheet" href="/show.css">
</head>
<body>
  <div id="stage">
    <div class="slide" data-slot="a"></div>
    <div class="slide" data-slot="b"></div>
  </div>

  <div id="caption" hidden>
    <span id="caption-sender"></span>
    <span id="caption-text"></span>
  </div>

  <div id="empty">
    <h1>Send us your photos</h1>
    <p id="empty-detail">They will appear here once an organiser approves them.</p>
  </div>

  <div id="offline" hidden>Reconnecting…</div>

  <!-- Keeps the display machine awake where the Wake Lock API is unavailable. -->
  <video id="keepawake" muted loop playsinline src="/keepawake.mp4"></video>

  <script src="/show.js"></script>
</body>
</html>
```

Generate the keep-awake video as a static file:

```bash
ffmpeg -f lavfi -i color=black:s=16x16:d=1 -c:v libx264 -pix_fmt yuv420p \
  -movflags +faststart src/EventPhotoBot/wwwroot/keepawake.mp4
```

A looping muted video is what stops a laptop sleeping in browsers without the Wake Lock API. One second at 16×16 is a few kilobytes.

- [ ] **Step 2: Write the styles**

Create `src/EventPhotoBot/wwwroot/show.css`:

```css
* { box-sizing: border-box; }

html, body {
  margin: 0;
  height: 100%;
  background: #000;
  overflow: hidden;
  font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
  color: #fff;
  cursor: none;
}

#stage { position: fixed; inset: 0; }

.slide {
  position: absolute;
  inset: 0;
  opacity: 0;
  transition: opacity var(--transition, 800ms) ease-in-out;
}

.slide.visible { opacity: 1; }

/* A blurred copy of the image fills the letterboxing rather than flat black. */
.slide .backdrop {
  position: absolute;
  inset: -5%;
  background-size: cover;
  background-position: center;
  filter: blur(48px) brightness(0.45);
}

.slide img {
  position: absolute;
  inset: 0;
  margin: auto;
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
}

#caption {
  position: fixed;
  left: 0; right: 0; bottom: 0;
  padding: 24px 32px calc(24px + env(safe-area-inset-bottom, 0px));
  background: linear-gradient(transparent, rgba(0, 0, 0, 0.75));
  font-size: clamp(1rem, 2vw, 1.6rem);
  display: flex;
  gap: 12px;
  align-items: baseline;
}

#caption-sender { font-weight: 600; opacity: 0.85; }
#caption-text { opacity: 0.95; }

#empty {
  position: fixed;
  inset: 0;
  display: grid;
  place-content: center;
  text-align: center;
  gap: 8px;
  padding: 32px;
}

#empty h1 { font-size: clamp(1.8rem, 5vw, 4rem); margin: 0; font-weight: 600; }
#empty p { font-size: clamp(1rem, 2vw, 1.5rem); margin: 0; opacity: 0.7; }
#empty[hidden] { display: none; }

#offline {
  position: fixed;
  top: calc(16px + env(safe-area-inset-top, 0px));
  right: 16px;
  padding: 8px 14px;
  border-radius: 999px;
  background: rgba(190, 60, 60, 0.85);
  font-size: 0.9rem;
}

#offline[hidden] { display: none; }
#caption[hidden] { display: none; }

#keepawake { position: fixed; width: 1px; height: 1px; opacity: 0; pointer-events: none; }
```

- [ ] **Step 3: Write the slideshow logic**

Create `src/EventPhotoBot/wwwroot/show.js`:

```javascript
(() => {
  'use strict';

  const POLL_MS = 2000;
  const POLL_BACKOFF_MS = 10000;
  const FAILURES_BEFORE_BACKOFF = 3;

  const stage = document.getElementById('stage');
  const slots = [...stage.querySelectorAll('.slide')];
  const captionEl = document.getElementById('caption');
  const captionSender = document.getElementById('caption-sender');
  const captionText = document.getElementById('caption-text');
  const emptyEl = document.getElementById('empty');
  const offlineEl = document.getElementById('offline');

  let manifest = null;
  let etag = null;
  let playlist = [];
  let cursor = 0;
  let activeSlot = 0;
  let advanceTimer = null;
  let failures = 0;
  let seenIds = new Set();

  const imageUrl = id => `/img/${encodeURIComponent(id)}/display`;

  // ---- polling -------------------------------------------------------------

  async function poll() {
    try {
      const headers = etag ? { 'If-None-Match': etag } : {};
      const response = await fetch('/api/manifest', { headers, cache: 'no-store' });

      if (response.status === 304) { onPollSuccess(); return; }
      if (!response.ok) throw new Error(`manifest ${response.status}`);

      etag = response.headers.get('ETag');
      applyManifest(await response.json());
      onPollSuccess();
    } catch {
      failures++;
      if (failures >= FAILURES_BEFORE_BACKOFF) offlineEl.hidden = false;
    } finally {
      // The last manifest stays in memory, so a failed poll changes nothing
      // on screen. Recovery needs no special handling: the next success carries
      // the current state.
      setTimeout(poll, failures >= FAILURES_BEFORE_BACKOFF ? POLL_BACKOFF_MS : POLL_MS);
    }
  }

  function onPollSuccess() {
    failures = 0;
    offlineEl.hidden = true;
  }

  // ---- reconciliation ------------------------------------------------------

  function applyManifest(next) {
    const first = manifest === null;
    manifest = next;

    document.documentElement.style.setProperty(
      '--transition', `${next.settings.transitionMs}ms`);

    const incoming = next.images;
    const incomingIds = new Set(incoming.map(i => i.id));

    // Images that vanished disappear after the current slide, not mid-slide.
    playlist = playlist.filter(i => incomingIds.has(i.id));

    const known = new Set(playlist.map(i => i.id));
    const fresh = incoming.filter(i => !known.has(i.id));

    if (first) {
      playlist = [...incoming];
    } else if (next.settings.newestFirstBoost) {
      // Newly approved images jump in within a slide or two, then rejoin the pool.
      const insertAt = Math.min(playlist.length, cursor + 1);
      const brandNew = fresh.filter(i => !seenIds.has(i.id));
      const rest = fresh.filter(i => seenIds.has(i.id));
      playlist.splice(insertAt, 0, ...brandNew);
      playlist.push(...rest);
    } else {
      playlist.push(...fresh);
    }

    incoming.forEach(i => seenIds.add(i.id));

    if (cursor >= playlist.length) cursor = 0;
    emptyEl.hidden = playlist.length > 0 || takeoverImage() !== null;

    if (first) advance();
  }

  function takeoverImage() {
    if (!manifest || !manifest.takeover) return null;
    const { id, until } = manifest.takeover;
    // Expiry is evaluated here: it triggers no write, so the ETag would not change.
    if (until && new Date(until) <= new Date()) return null;
    return manifest.images.find(i => i.id === id)
        ?? { id, width: 0, height: 0, caption: null, senderName: null };
  }

  // ---- rendering -----------------------------------------------------------

  function render(image) {
    const slot = slots[activeSlot];
    const next = slots[1 - activeSlot];
    const url = imageUrl(image.id);

    next.innerHTML =
      `<div class="backdrop" style="background-image:url('${url}')"></div>` +
      `<img src="${url}" alt="">`;

    next.classList.add('visible');
    slot.classList.remove('visible');
    activeSlot = 1 - activeSlot;

    const hasCaption = Boolean(image.caption || image.senderName);
    captionEl.hidden = !hasCaption;
    captionSender.textContent = image.senderName ?? '';
    captionText.textContent = image.caption ?? '';
  }

  function preload(count) {
    for (let i = 1; i <= count; i++) {
      const upcoming = playlist[(cursor + i) % playlist.length];
      if (upcoming) new Image().src = imageUrl(upcoming.id);
    }
  }

  function advance() {
    clearTimeout(advanceTimer);

    const takeover = takeoverImage();
    if (takeover) {
      render(takeover);
      // Re-check often so the screen restores within a slide of the takeover clearing.
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    if (playlist.length === 0) {
      emptyEl.hidden = false;
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    emptyEl.hidden = true;
    render(playlist[cursor % playlist.length]);
    cursor = (cursor + 1) % playlist.length;
    preload(2);

    const seconds = manifest?.settings?.slideSeconds ?? 8;
    advanceTimer = setTimeout(advance, seconds * 1000);
  }

  // ---- keep the display awake ---------------------------------------------

  async function keepAwake() {
    try {
      if ('wakeLock' in navigator) {
        let lock = await navigator.wakeLock.request('screen');
        document.addEventListener('visibilitychange', async () => {
          if (document.visibilityState === 'visible') {
            lock = await navigator.wakeLock.request('screen');
          }
        });
        return;
      }
    } catch { /* fall through to the video trick */ }

    const video = document.getElementById('keepawake');
    video.play().catch(() => { /* autoplay blocked; OS sleep settings are the backstop */ });
  }

  keepAwake();
  poll();
})();
```

Two behaviours worth reading rather than skimming.

`takeoverImage` evaluates `until` against the browser's clock. That is decision 2 from the top of this plan: expiry changes what should be on screen but triggers no write, so the ETag would not move and a server-side check alone would leave a stale takeover up until the next unrelated change.

The `seenIds` set distinguishes a genuinely new image from one that merely reappeared after being hidden and shown again. Without it, un-hiding an old photo would boost it to the front as though it had just arrived.

- [ ] **Step 4: Verify manually**

Run the app locally against a fake bucket is not practical, so verify after Task 16's deploy. At minimum, before moving on, confirm the file parses:

```bash
node --check src/EventPhotoBot/wwwroot/show.js
```

Expected: no output, exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/EventPhotoBot/wwwroot
git commit -m "feat: slideshow page with polling, reconciliation and takeover"
```

---

### Task 13: Admin pages

**Files:**
- Replace: `src/EventPhotoBot/wwwroot/admin/queue.html`, `admin/images.html`, `admin/settings.html`
- Create: `src/EventPhotoBot/wwwroot/admin/admin.css`, `src/EventPhotoBot/wwwroot/admin/admin.js`

**Interfaces:**
- Consumes: every route from Task 11.
- Produces: nothing other tasks consume.

- [ ] **Step 1: Write the shared stylesheet**

Create `src/EventPhotoBot/wwwroot/admin/admin.css`:

```css
* { box-sizing: border-box; }

:root {
  color-scheme: dark;
  --bg: #121212;
  --panel: #1c1c1e;
  --line: #333;
  --text: #f2f2f2;
  --muted: #a0a0a5;
  --accent: #2f6fed;
  --danger: #d9534f;
  --ok: #3da35d;
}

body {
  margin: 0;
  padding-bottom: calc(24px + env(safe-area-inset-bottom, 0px));
  background: var(--bg);
  color: var(--text);
  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
}

nav {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  gap: 4px;
  padding: calc(8px + env(safe-area-inset-top, 0px)) 8px 8px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
  overflow-x: auto;
}

nav a {
  padding: 10px 14px;
  border-radius: 8px;
  color: var(--muted);
  text-decoration: none;
  white-space: nowrap;
}

nav a.active { background: var(--bg); color: var(--text); }

.badge {
  display: inline-block;
  min-width: 20px;
  margin-left: 6px;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--accent);
  color: #fff;
  font-size: 0.8rem;
  text-align: center;
}

.badge:empty, .badge[data-count="0"] { display: none; }

main { padding: 16px; max-width: 1100px; margin: 0 auto; }

#takeover-banner {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin: 0 0 16px;
  padding: 12px 16px;
  border-radius: 10px;
  background: #4a3410;
  border: 1px solid #7a5716;
}

#takeover-banner[hidden] { display: none; }

.queue-item {
  margin-bottom: 24px;
  padding: 12px;
  border-radius: 12px;
  background: var(--panel);
}

.queue-item img { width: 100%; border-radius: 8px; display: block; }

.row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 12px; }

button {
  font: inherit;
  padding: 10px 16px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--panel);
  color: var(--text);
  cursor: pointer;
}

button.primary { background: var(--ok); border-color: transparent; color: #fff; }
button.danger { background: var(--danger); border-color: transparent; color: #fff; }
button:disabled { opacity: 0.5; cursor: default; }

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 12px;
}

.grid figure { margin: 0; background: var(--panel); border-radius: 10px; overflow: hidden; }
.grid img { width: 100%; aspect-ratio: 1; object-fit: cover; display: block; }
.grid .row { padding: 8px; margin: 0; gap: 4px; }
.grid button { padding: 6px 10px; font-size: 0.85rem; }

label { display: block; margin: 16px 0 4px; color: var(--muted); font-size: 0.9rem; }
input, select { font: inherit; width: 100%; padding: 10px; border-radius: 8px;
                border: 1px solid var(--line); background: var(--bg); color: var(--text); }
input[type="checkbox"] { width: auto; }

table { width: 100%; border-collapse: collapse; margin-top: 8px; }
td, th { padding: 8px; border-bottom: 1px solid var(--line); text-align: left; }

.muted { color: var(--muted); font-size: 0.9rem; }
.empty { color: var(--muted); padding: 32px 0; text-align: center; }
```

- [ ] **Step 2: Write the shared script**

Create `src/EventPhotoBot/wwwroot/admin/admin.js`:

```javascript
(() => {
  'use strict';

  const POLL_MS = 2000;
  let etag = null;
  let manifest = null;
  const listeners = [];

  window.Admin = {
    onManifest(callback) { listeners.push(callback); if (manifest) callback(manifest); },
    get manifest() { return manifest; },
    api,
    imageUrl: (id, size) => `/img/${encodeURIComponent(id)}/${size}`,
  };

  async function api(method, path, body) {
    const options = { method, headers: {} };
    if (body instanceof FormData) {
      options.body = body;
    } else if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    const response = await fetch(path, options);
    if (response.status === 401) { location.href = '/login'; return null; }
    if (!response.ok) {
      const detail = await response.text();
      alert(`That did not work (${response.status}). ${detail}`);
      return null;
    }
    // Refresh straight away rather than waiting for the next poll.
    await pollOnce(true);
    return response;
  }

  async function pollOnce(force) {
    const headers = force || !etag ? {} : { 'If-None-Match': etag };
    const response = await fetch('/api/manifest', { headers, cache: 'no-store' });
    if (response.status === 304) return;
    if (!response.ok) return;

    etag = response.headers.get('ETag');
    manifest = await response.json();

    for (const badge of document.querySelectorAll('.badge')) {
      badge.textContent = manifest.pendingCount || '';
      badge.dataset.count = manifest.pendingCount;
    }
    renderTakeoverBanner();
    listeners.forEach(fn => fn(manifest));
  }

  function renderTakeoverBanner() {
    const banner = document.getElementById('takeover-banner');
    if (!banner) return;

    const takeover = manifest.takeover;
    const active = takeover && (!takeover.until || new Date(takeover.until) > new Date());
    banner.hidden = !active;
    if (!active) return;

    const until = takeover.until
      ? `until ${new Date(takeover.until).toLocaleTimeString()}`
      : 'until you clear it';
    banner.innerHTML =
      `<strong>One image is holding the screen</strong><span class="muted">${until}</span>`;

    const clear = document.createElement('button');
    clear.className = 'danger';
    clear.textContent = 'Clear takeover';
    clear.onclick = () => api('DELETE', '/api/takeover');
    banner.appendChild(clear);
  }

  async function loop() {
    try { await pollOnce(false); } catch { /* keep polling */ }
    setTimeout(loop, POLL_MS);
  }

  for (const link of document.querySelectorAll('nav a')) {
    if (link.getAttribute('href') === location.pathname) link.classList.add('active');
  }

  loop();
})();
```

- [ ] **Step 3: Write the approval queue**

Replace `src/EventPhotoBot/wwwroot/admin/queue.html`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Queue</title>
  <link rel="stylesheet" href="/admin/admin.css">
</head>
<body>
  <nav>
    <a href="/admin/queue">Queue<span class="badge"></span></a>
    <a href="/admin/images">Images</a>
    <a href="/admin/settings">Settings</a>
    <a href="/show">Screen</a>
  </nav>

  <main>
    <div id="takeover-banner" hidden></div>
    <p class="muted">Press <kbd>A</kbd> to approve or <kbd>R</kbd> to reject the top photo.</p>
    <div id="queue"></div>
  </main>

  <script src="/admin/admin.js"></script>
  <script>
    // The manifest carries only approved images, so the queue needs the pending
    // ones separately. /api/manifest reports the count; this page lists them.
    let pending = [];

    async function loadPending() {
      const response = await fetch('/api/images?status=pending', { cache: 'no-store' });
      if (!response.ok) return;
      pending = await response.json();
      render();
    }

    function render() {
      const container = document.getElementById('queue');
      if (pending.length === 0) {
        container.innerHTML = '<p class="empty">Nothing waiting. Enjoy the party.</p>';
        return;
      }
      container.innerHTML = pending.map(image => `
        <div class="queue-item" data-id="${image.id}">
          <img src="${Admin.imageUrl(image.id, 'display')}" alt="" loading="lazy">
          <div class="row">
            <strong>${image.senderName ?? 'Unknown'}</strong>
            <span class="muted">${image.caption ?? ''}</span>
          </div>
          <div class="row">
            <button class="primary" onclick="decide('${image.id}','approved')">Approve</button>
            <button class="danger" onclick="decide('${image.id}','rejected')">Reject</button>
          </div>
        </div>`).join('');
    }

    async function decide(id, status) {
      await Admin.api('POST', `/api/images/${id}/status`, { status });
      pending = pending.filter(i => i.id !== id);
      render();
    }

    document.addEventListener('keydown', event => {
      if (pending.length === 0) return;
      const key = event.key.toLowerCase();
      if (key === 'a') decide(pending[0].id, 'approved');
      if (key === 'r') decide(pending[0].id, 'rejected');
    });

    Admin.onManifest(() => loadPending());
    loadPending();
  </script>
</body>
</html>
```

This page needs a route the API does not yet have. Add it to `MapApi` in `src/EventPhotoBot/Web/ApiEndpoints.cs`:

```csharp
app.MapGet("/api/images", (string? status, StateStore store) =>
{
    var images = store.Snapshot.Images.Values.AsEnumerable();

    if (status is not null)
    {
        if (!Enum.TryParse<ImageStatus>(status, ignoreCase: true, out var wanted))
            return Results.BadRequest(new { error = "Unknown status." });
        images = images.Where(i => i.Status == wanted);
    }

    return Results.Ok(images
        .OrderByDescending(i => i.SortKey, StringComparer.Ordinal)
        .Select(i => new
        {
            i.Id, i.SenderName, i.Caption, i.Width, i.Height,
            Status = i.Status.ToString().ToLowerInvariant(),
            Pin = i.Pin.ToString().ToLowerInvariant(),
            i.ReceivedAt,
        }));
});
```

Like the manifest, this reads only from memory.

- [ ] **Step 4: Write the image management page**

Replace `src/EventPhotoBot/wwwroot/admin/images.html`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Images</title>
  <link rel="stylesheet" href="/admin/admin.css">
</head>
<body>
  <nav>
    <a href="/admin/queue">Queue<span class="badge"></span></a>
    <a href="/admin/images">Images</a>
    <a href="/admin/settings">Settings</a>
    <a href="/show">Screen</a>
  </nav>

  <main>
    <div id="takeover-banner" hidden></div>

    <div class="row">
      <label for="filter" style="margin:0">Show</label>
      <select id="filter" style="width:auto" onchange="load()">
        <option value="approved">Approved</option>
        <option value="hidden">Hidden</option>
        <option value="pending">Pending</option>
        <option value="rejected">Rejected</option>
        <option value="">Everything</option>
      </select>
      <input type="file" id="upload" accept="image/*" style="width:auto">
      <button onclick="uploadFile()">Upload</button>
    </div>

    <div class="grid" id="grid"></div>
  </main>

  <script src="/admin/admin.js"></script>
  <script>
    async function load() {
      const status = document.getElementById('filter').value;
      const query = status ? `?status=${status}` : '';
      const response = await fetch(`/api/images${query}`, { cache: 'no-store' });
      if (!response.ok) return;
      render(await response.json());
    }

    function render(images) {
      const grid = document.getElementById('grid');
      if (images.length === 0) {
        grid.innerHTML = '<p class="empty">Nothing here.</p>';
        return;
      }
      grid.innerHTML = images.map(image => `
        <figure data-id="${image.id}">
          <img src="${Admin.imageUrl(image.id, 'thumb')}" alt="" loading="lazy">
          <div class="row">
            <button onclick="setStatus('${image.id}','${image.status === 'hidden' ? 'approved' : 'hidden'}')">
              ${image.status === 'hidden' ? 'Show' : 'Hide'}
            </button>
            <button onclick="setPin('${image.id}','${image.pin === 'recurring' ? 'none' : 'recurring'}')">
              ${image.pin === 'recurring' ? 'Unpin' : 'Pin'}
            </button>
            <button onclick="takeover('${image.id}')">Take over</button>
            <button class="danger" onclick="remove('${image.id}')">Delete</button>
          </div>
        </figure>`).join('');
    }

    const setStatus = (id, status) => Admin.api('POST', `/api/images/${id}/status`, { status }).then(load);
    const setPin = (id, pin) => Admin.api('POST', `/api/images/${id}/pin`, { pin }).then(load);

    async function remove(id) {
      if (!confirm('Delete this photo? It is removed from storage too, and cannot be undone.')) return;
      await Admin.api('DELETE', `/api/images/${id}`);
      load();
    }

    async function takeover(id) {
      const choice = prompt(
        'Take over the screen for how long?\n5, 15 or 60 minutes, or leave blank for "until I clear it".', '15');
      if (choice === null) return;
      const minutes = choice.trim() === '' ? null : Number(choice);
      if (minutes !== null && (!Number.isFinite(minutes) || minutes <= 0)) {
        alert('Enter a number of minutes, or leave it blank.');
        return;
      }
      await Admin.api('PUT', '/api/takeover', { imageId: id, minutes });
      load();
    }

    async function uploadFile() {
      const input = document.getElementById('upload');
      if (!input.files.length) return;
      const form = new FormData();
      form.append('file', input.files[0]);
      await Admin.api('POST', '/api/images', form);
      input.value = '';
      load();
    }

    Admin.onManifest(() => load());
    load();
  </script>
</body>
</html>
```

- [ ] **Step 5: Write the settings page**

Replace `src/EventPhotoBot/wwwroot/admin/settings.html`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Settings</title>
  <link rel="stylesheet" href="/admin/admin.css">
</head>
<body>
  <nav>
    <a href="/admin/queue">Queue<span class="badge"></span></a>
    <a href="/admin/images">Images</a>
    <a href="/admin/settings">Settings</a>
    <a href="/show">Screen</a>
  </nav>

  <main>
    <div id="takeover-banner" hidden></div>

    <h2>Slideshow</h2>
    <label for="slideSeconds">Seconds per image</label>
    <input id="slideSeconds" type="number" min="2" max="120">
    <label for="transitionMs">Cross-fade (ms)</label>
    <input id="transitionMs" type="number" min="0" max="5000">
    <label for="order">Order</label>
    <select id="order">
      <option value="shuffle">Shuffle</option>
      <option value="newest-first">Newest first</option>
    </select>
    <label><input id="newestFirstBoost" type="checkbox"> Jump new photos to the front</label>
    <label for="recurringEvery">Show a pinned image every N photos</label>
    <input id="recurringEvery" type="number" min="1" max="100">
    <div class="row"><button class="primary" onclick="saveSlideshow()">Save</button></div>

    <h2>Senders</h2>
    <label><input id="autoApproveTrusted" type="checkbox">
      Trusted senders skip the queue</label>
    <label><input id="pairingMode" type="checkbox">
      Pairing mode — anyone who messages the bot gets their id back</label>
    <div class="row"><button class="primary" onclick="saveSenders()">Save</button></div>

    <h3>Whitelist</h3>
    <table id="whitelist"></table>
    <div class="row">
      <input id="newId" type="number" placeholder="Telegram id" style="width:auto">
      <input id="newName" type="text" placeholder="Name" style="width:auto">
      <button onclick="addEntry()">Add</button>
    </div>

    <h3>Seen while pairing</h3>
    <table id="seen"></table>
    <div class="row"><button onclick="clearSeen()">Clear the list</button></div>
  </main>

  <script src="/admin/admin.js"></script>
  <script>
    let settings = null;
    let whitelist = [];
    let seen = [];

    async function load() {
      const response = await fetch('/api/settings', { cache: 'no-store' });
      if (!response.ok) return;
      settings = await response.json();
      whitelist = settings.whitelist ?? [];
      seen = settings.seenSenders ?? [];

      for (const key of ['slideSeconds', 'transitionMs', 'recurringEvery']) {
        document.getElementById(key).value = settings[key];
      }
      document.getElementById('order').value = settings.order;
      document.getElementById('newestFirstBoost').checked = settings.newestFirstBoost;
      document.getElementById('autoApproveTrusted').checked = settings.autoApproveTrusted;
      document.getElementById('pairingMode').checked = settings.pairingMode;
      renderWhitelist();
      renderSeen();
    }

    function renderWhitelist() {
      document.getElementById('whitelist').innerHTML =
        whitelist.length === 0
          ? '<tr><td class="muted">Nobody yet. Turn on pairing mode and share the bot.</td></tr>'
          : whitelist.map((entry, index) => `
            <tr>
              <td>${entry.name}</td>
              <td class="muted">${entry.id}</td>
              <td><label><input type="checkbox" ${entry.trusted ? 'checked' : ''}
                    onchange="toggleTrusted(${index}, this.checked)"> trusted</label></td>
              <td><button class="danger" onclick="removeEntry(${index})">Remove</button></td>
            </tr>`).join('');
    }

    function renderSeen() {
      document.getElementById('seen').innerHTML =
        seen.length === 0
          ? '<tr><td class="muted">Nobody has messaged the bot while pairing was on.</td></tr>'
          : seen.map(sender => `
            <tr>
              <td>${sender.name}</td>
              <td class="muted">${sender.id}</td>
              <td><button onclick="addSeen(${sender.id}, '${sender.name.replace(/'/g, "\\'")}')">
                Add to whitelist</button></td>
            </tr>`).join('');
    }

    const saveWhitelist = () => Admin.api('PATCH', '/api/settings', { whitelist }).then(load);

    function toggleTrusted(index, trusted) { whitelist[index].trusted = trusted; saveWhitelist(); }
    function removeEntry(index) { whitelist.splice(index, 1); saveWhitelist(); }

    function addEntry() {
      const id = Number(document.getElementById('newId').value);
      const name = document.getElementById('newName').value.trim() || String(id);
      if (!Number.isFinite(id) || id <= 0) { alert('Enter a numeric Telegram id.'); return; }
      if (whitelist.some(e => e.id === id)) { alert('Already on the list.'); return; }
      whitelist.push({ id, name, trusted: false });
      document.getElementById('newId').value = '';
      document.getElementById('newName').value = '';
      saveWhitelist();
    }

    function addSeen(id, name) {
      if (whitelist.some(e => e.id === id)) return;
      whitelist.push({ id, name, trusted: false });
      saveWhitelist();
    }

    const clearSeen = () =>
      Admin.api('PATCH', '/api/settings', { clearSeenSenders: true }).then(load);

    const saveSlideshow = () => Admin.api('PATCH', '/api/settings', {
      slideSeconds: Number(document.getElementById('slideSeconds').value),
      transitionMs: Number(document.getElementById('transitionMs').value),
      order: document.getElementById('order').value,
      newestFirstBoost: document.getElementById('newestFirstBoost').checked,
      recurringEvery: Number(document.getElementById('recurringEvery').value),
    }).then(load);

    const saveSenders = () => Admin.api('PATCH', '/api/settings', {
      autoApproveTrusted: document.getElementById('autoApproveTrusted').checked,
      pairingMode: document.getElementById('pairingMode').checked,
    }).then(load);

    load();
  </script>
</body>
</html>
```

This page needs one more read route. Add it to `MapApi`:

```csharp
app.MapGet("/api/settings", (StateStore store) =>
{
    var s = store.Snapshot.Settings;
    return Results.Ok(new
    {
        s.SlideSeconds,
        s.TransitionMs,
        Order = s.Order == SlideOrder.NewestFirst ? "newest-first" : "shuffle",
        s.NewestFirstBoost,
        s.RecurringEvery,
        s.AutoApproveTrusted,
        s.PairingMode,
        s.Whitelist,
        s.SeenSenders,
        s.TakeoverImageId,
        s.TakeoverUntil,
    });
});
```

- [ ] **Step 6: Check the scripts parse and the tests still pass**

```bash
node --check src/EventPhotoBot/wwwroot/admin/admin.js
dotnet test tests/EventPhotoBot.Tests
```

Expected: no output from `node --check`; all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: admin queue, image management and settings pages"
```

---

### Task 14: Container image

**Files:**
- Create: `Dockerfile`, `.dockerignore`

**Interfaces:**
- Consumes: the built application.
- Produces: an image that listens on `$PORT` and answers `/healthz`.

- [ ] **Step 1: Write the Dockerfile**

Create `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EventPhotoBot.sln ./
COPY src/EventPhotoBot/EventPhotoBot.csproj src/EventPhotoBot/
COPY tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj tests/EventPhotoBot.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/EventPhotoBot/EventPhotoBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Cloud Run supplies PORT and expects the container to listen on it.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# Run as a non-root user. The app writes nothing to the filesystem — all state
# and all bytes live in the bucket — so there is nothing to grant write access to.
USER $APP_UID

ENTRYPOINT ["dotnet", "EventPhotoBot.dll"]
```

Restoring before copying the rest means a source-only change reuses the restore layer, which matters when iterating on a deploy.

`ASPNETCORE_URLS` is pinned to 8080 rather than read from `$PORT` because Cloud Run defaults to 8080 and the Terraform in Task 15 does not change it. If the port is ever changed there, change it here too.

- [ ] **Step 2: Write the .dockerignore**

Create `.dockerignore`:

```
**/bin/
**/obj/
.git/
docs/
infra/
*.md
```

Excluding `bin` and `obj` is not cosmetic: copying a host-built `obj` into the image produces restore errors that are confusing to diagnose.

- [ ] **Step 3: Build and smoke-test the image locally**

```bash
docker build -t eventphotobot:local .
docker run --rm -p 8080:8080 \
  -e BUCKET_NAME=x -e EVENT_NAME=x -e TELEGRAM_BOT_TOKEN=x \
  -e TELEGRAM_WEBHOOK_SECRET=x -e TELEGRAM_WEBHOOK_PATH=x \
  -e ADMIN_PASSWORD=x -e COOKIE_SIGNING_KEY=x \
  eventphotobot:local
```

Expected: the container starts and logs the "Secret … loaded" lines. It will then fail to reach GCS when loading state, which is correct — there are no credentials. The point of this step is that the image builds, starts, and reads its configuration.

To confirm the failure is the expected one, check the log names the storage call rather than a missing configuration value.

- [ ] **Step 4: Confirm fail-fast works**

```bash
docker run --rm eventphotobot:local
```

Expected: exits immediately with `Missing required configuration: BUCKET_NAME, EVENT_NAME, ...`. This is the spec's "fails to start rather than starting in a degraded state", verified.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "build: container image"
```

---

### Task 15: Terraform

**Files:**
- Create: `infra/main.tf`, `infra/variables.tf`, `infra/outputs.tf`, `infra/terraform.tfvars.example`

**Interfaces:**
- Consumes: a container image digest in Artifact Registry.
- Produces: outputs `service_url`, `bucket_name`, `service_account_email`.

- [ ] **Step 1: Write the variables**

Create `infra/variables.tf`:

```hcl
variable "project_id" {
  type        = string
  description = "GCP project. This project is created for the event and destroyed after it."
}

variable "region" {
  type        = string
  default     = "europe-north1"
  description = "Latency, and keeping the data in the EU."
}

variable "name" {
  type        = string
  default     = "eventphoto"
  description = "Prefix for every resource name."
}

variable "event_name" {
  type        = string
  description = "Shown on the slideshow's empty state."
}

variable "image_digest" {
  type        = string
  description = "Full image reference pinned by digest, e.g. europe-north1-docker.pkg.dev/PROJECT/eventphoto/app@sha256:abc123. Pinned by digest rather than tag so terraform apply is honest about what changes."
}
```

- [ ] **Step 2: Write the main configuration**

Create `infra/main.tf`:

```hcl
terraform {
  required_version = ">= 1.9"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}

# ---------------------------------------------------------------------------
# APIs
# ---------------------------------------------------------------------------

resource "google_project_service" "apis" {
  for_each = toset([
    "run.googleapis.com",
    "artifactregistry.googleapis.com",
    "secretmanager.googleapis.com",
    "storage.googleapis.com",
    "iamcredentials.googleapis.com",
  ])

  service = each.value

  # Disabling APIs on destroy is a common source of hung or failed teardowns.
  disable_on_destroy = false
}

# ---------------------------------------------------------------------------
# Storage — images and state.json
# ---------------------------------------------------------------------------

resource "google_storage_bucket" "images" {
  name     = "${var.name}-${var.project_id}"
  location = var.region

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  versioning { enabled = false }

  # force_destroy so teardown removes the bucket rather than failing on contents.
  force_destroy = true

  lifecycle_rule {
    condition {
      age            = 30
      matches_prefix = ["originals/", "display/", "thumbs/"]
    }
    action { type = "Delete" }
  }

  depends_on = [google_project_service.apis]
}

# state/ is deliberately outside the lifecycle rule above. Each write creates a
# new object with a fresh creation time so it would survive an active event
# either way, but an unscoped age rule on the object holding all the metadata is
# not something to leave to chance.

# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

resource "google_artifact_registry_repository" "images" {
  location      = var.region
  repository_id = var.name
  format        = "DOCKER"

  depends_on = [google_project_service.apis]
}

# ---------------------------------------------------------------------------
# Secrets — resources only. Values are added outside Terraform:
#   gcloud secrets versions add eventphoto-bot-token --data-file=-
# A secret passed as a Terraform variable ends up in plaintext in state.
# ---------------------------------------------------------------------------

locals {
  secret_ids = {
    bot_token      = "${var.name}-bot-token"
    webhook_secret = "${var.name}-webhook-secret"
    webhook_path   = "${var.name}-webhook-path"
    admin_password = "${var.name}-admin-password"
    cookie_key     = "${var.name}-cookie-key"
  }
}

resource "google_secret_manager_secret" "secrets" {
  for_each  = local.secret_ids
  secret_id = each.value

  replication {
    user_managed {
      replicas { location = var.region }
    }
  }

  depends_on = [google_project_service.apis]
}

# ---------------------------------------------------------------------------
# Runtime identity
# ---------------------------------------------------------------------------

resource "google_service_account" "runtime" {
  account_id   = "${var.name}-run"
  display_name = "Event photo bot runtime"
}

resource "google_storage_bucket_iam_member" "objects" {
  bucket = google_storage_bucket.images.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.runtime.email}"
}

resource "google_secret_manager_secret_iam_member" "access" {
  for_each  = google_secret_manager_secret.secrets
  secret_id = each.value.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.runtime.email}"
}

# ---------------------------------------------------------------------------
# The service
# ---------------------------------------------------------------------------

resource "google_cloud_run_v2_service" "app" {
  name     = var.name
  location = var.region

  deletion_protection = false
  ingress             = "INGRESS_TRAFFIC_ALL"

  template {
    service_account = google_service_account.runtime.email

    # Nothing holds a connection open, so nothing pins an instance: Cloud Run
    # bills request time only and the service costs nothing between events.
    scaling {
      min_instance_count = 0
      max_instance_count = 1
    }

    # A 20 MB getFile plus derivatives, with margin.
    timeout = "120s"

    containers {
      image = var.image_digest

      resources {
        limits = {
          cpu    = "1"
          memory = "1Gi"
        }
        cpu_idle          = true # billed for request time only
        startup_cpu_boost = true # keeps the cold start to a few seconds
      }

      env {
        name  = "BUCKET_NAME"
        value = google_storage_bucket.images.name
      }

      env {
        name  = "EVENT_NAME"
        value = var.event_name
      }

      dynamic "env" {
        for_each = {
          TELEGRAM_BOT_TOKEN      = local.secret_ids.bot_token
          TELEGRAM_WEBHOOK_SECRET = local.secret_ids.webhook_secret
          TELEGRAM_WEBHOOK_PATH   = local.secret_ids.webhook_path
          ADMIN_PASSWORD          = local.secret_ids.admin_password
          COOKIE_SIGNING_KEY      = local.secret_ids.cookie_key
        }

        content {
          name = env.key
          value_source {
            secret_key_ref {
              secret  = env.value
              version = "latest"
            }
          }
        }
      }

      startup_probe {
        http_get { path = "/healthz" }
        initial_delay_seconds = 3
        period_seconds        = 3
        failure_threshold     = 10
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_iam_member.access,
    google_storage_bucket_iam_member.objects,
  ]
}

# Telegram and guests need to reach the service; the app's own password is the gate.
resource "google_cloud_run_v2_service_iam_member" "public" {
  name     = google_cloud_run_v2_service.app.name
  location = google_cloud_run_v2_service.app.location
  role     = "roles/run.invoker"
  member   = "allUsers"
}

# ---------------------------------------------------------------------------
# Cost guard. The event costs well under a euro; this exists so a forgotten
# teardown cannot quietly become a monthly bill.
# ---------------------------------------------------------------------------

# Requires a billing account id and the billingbudgets API. Left commented
# because it is the one resource here that touches billing-account-level IAM,
# which the event project's owner may not hold. Enable it if you do.
#
# resource "google_billing_budget" "guard" {
#   billing_account = var.billing_account
#   display_name    = "${var.name} guard"
#   budget_filter { projects = ["projects/${var.project_id}"] }
#   amount { specified_amount { currency_code = "EUR" units = "20" } }
#   threshold_rules { threshold_percent = 0.5 }
#   threshold_rules { threshold_percent = 1.0 }
# }
```

There is no `google_firestore_database` and no `roles/datastore.user`, which is the point of keeping state in the bucket: the bucket is the only stateful resource, and it destroys cleanly with `force_destroy`.

- [ ] **Step 3: Write the outputs**

Create `infra/outputs.tf`:

```hcl
output "service_url" {
  value       = google_cloud_run_v2_service.app.uri
  description = "Where the slideshow and admin pages live. Also the webhook base."
}

output "bucket_name" {
  value       = google_storage_bucket.images.name
  description = "Download originals/ from here before destroying."
}

output "service_account_email" {
  value = google_service_account.runtime.email
}
```

- [ ] **Step 4: Write the example tfvars**

Create `infra/terraform.tfvars.example`:

```hcl
project_id   = "my-event-project"
event_name   = "Summer Party"
image_digest = "europe-north1-docker.pkg.dev/my-event-project/eventphoto/app@sha256:REPLACE"
```

- [ ] **Step 5: Validate**

```bash
terraform -chdir=infra init
terraform -chdir=infra validate
terraform -chdir=infra fmt -check
```

Expected: `Success! The configuration is valid.` and no formatting diff.

- [ ] **Step 6: Commit**

```bash
git add infra
git commit -m "infra: Terraform for bucket, secrets, registry and Cloud Run"
```

---

### Task 16: Deploy script, webhook registration, and acceptance

The webhook URL is only known after the service exists, so registration is a step after apply — a script rather than a `null_resource`, because a script is easier to reason about and easier to rerun.

**Files:**
- Create: `infra/deploy.ps1`
- Create: `docs/RUNBOOK.md`

**Interfaces:**
- Consumes: everything.
- Produces: a deployed service with its webhook registered.

- [ ] **Step 1: Write the deploy script**

Create `infra/deploy.ps1`:

```powershell
#requires -Version 7
<#
  Build, push, apply, register the webhook. Safe to rerun.
  Secret VALUES never pass through Terraform: they are set here with gcloud,
  because a secret passed as a Terraform variable ends up in plaintext in state.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProjectId,
    [Parameter(Mandatory)][string] $EventName,
    [string] $Region = 'europe-north1',
    [string] $Name = 'eventphoto',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$registry = "$Region-docker.pkg.dev/$ProjectId/$Name"

Write-Host '==> Bootstrapping registry, bucket and secrets (first apply may partially fail; rerun)' -ForegroundColor Cyan
terraform -chdir="$PSScriptRoot" init
terraform -chdir="$PSScriptRoot" apply `
    -target=google_artifact_registry_repository.images `
    -target=google_secret_manager_secret.secrets `
    -var "project_id=$ProjectId" -var "event_name=$EventName" -var "region=$Region" `
    -var 'image_digest=placeholder'

Write-Host '==> Secret values' -ForegroundColor Cyan
Write-Host 'Add any that are missing, then rerun. These commands are interactive on purpose:'
Write-Host "  gcloud secrets versions add $Name-bot-token      --data-file=- --project $ProjectId"
Write-Host "  gcloud secrets versions add $Name-webhook-secret --data-file=- --project $ProjectId"
Write-Host "  gcloud secrets versions add $Name-webhook-path   --data-file=- --project $ProjectId"
Write-Host "  gcloud secrets versions add $Name-admin-password --data-file=- --project $ProjectId"
Write-Host "  gcloud secrets versions add $Name-cookie-key     --data-file=- --project $ProjectId"
Write-Host ''
Write-Host 'Generate the three random ones with:  openssl rand -hex 32'
Write-Host ''

if (-not $SkipBuild) {
    Write-Host '==> Build and push' -ForegroundColor Cyan
    gcloud auth configure-docker "$Region-docker.pkg.dev" --quiet --project $ProjectId
    docker build -t "$registry/app:latest" $repo
    docker push "$registry/app:latest"
}

# Pin by digest so terraform apply is honest about what changes.
$digest = (docker inspect --format='{{index .RepoDigests 0}}' "$registry/app:latest")
Write-Host "==> Image: $digest" -ForegroundColor Cyan

Write-Host '==> Apply' -ForegroundColor Cyan
terraform -chdir="$PSScriptRoot" apply `
    -var "project_id=$ProjectId" -var "event_name=$EventName" -var "region=$Region" `
    -var "image_digest=$digest"

$serviceUrl = terraform -chdir="$PSScriptRoot" output -raw service_url

Write-Host '==> Registering the Telegram webhook' -ForegroundColor Cyan
$botToken     = gcloud secrets versions access latest --secret "$Name-bot-token" --project $ProjectId
$webhookPath  = gcloud secrets versions access latest --secret "$Name-webhook-path" --project $ProjectId
$webhookSecret= gcloud secrets versions access latest --secret "$Name-webhook-secret" --project $ProjectId

$response = Invoke-RestMethod -Method Post -Uri "https://api.telegram.org/bot$botToken/setWebhook" -Body @{
    url                  = "$serviceUrl/tg/$webhookPath"
    secret_token         = $webhookSecret
    max_connections      = 4
    drop_pending_updates = 'true'
}

if (-not $response.ok) { throw "setWebhook failed: $($response.description)" }

Write-Host ''
Write-Host "Slideshow: $serviceUrl/show"    -ForegroundColor Green
Write-Host "Admin:     $serviceUrl/admin/queue" -ForegroundColor Green
Write-Host 'Webhook registered.' -ForegroundColor Green
```

The script reads secret values to register the webhook but never writes them anywhere; `$botToken` lives in the shell for the length of one call.

- [ ] **Step 2: Deploy**

```bash
pwsh infra/deploy.ps1 -ProjectId my-event-project -EventName "Summer Party"
```

Expected: the script prints the slideshow and admin URLs and confirms the webhook.

- [ ] **Step 3: Work through the acceptance criteria**

These come straight from the spec. Every one is a manual check against the deployed service — the unit tests cover the logic, these cover the system.

- [ ] Open the admin settings page, turn on pairing mode, message the bot from a phone, and confirm the reply carries your numeric id and that you appear under "Seen while pairing".
- [ ] Add yourself with one tap, turn pairing mode off, and confirm a photo now reaches the queue. **Adding a sender takes effect on their next message, with no redeploy.**
- [ ] **A whitelisted sender's photo appears in the queue within five seconds of sending.** Time it. If an album of five is slower, that is the serial-delivery effect described in the Telegram section.
- [ ] **A non-whitelisted sender gets a decline and nothing is stored.** Check the bucket has no new objects.
- [ ] **Approving an image makes it appear on an already-running slideshow within two seconds, without a reload.**
- [ ] **Hiding or deleting an image removes it from the slideshow within two seconds, without a reload.**
- [ ] **A manifest poll that finds no change returns 304 and performs no GCS read.** Confirm in the browser's network tab that steady-state polls are 304, and that Cloud Run's logs show no storage calls between changes.
- [ ] **A takeover image displaces the slideshow within one slide and restores it when cleared.**
- [ ] **Setting takeover on a second image replaces the first rather than leaving two claims.**
- [ ] **Deleting or hiding the image that holds takeover clears the takeover in the same write.**
- [ ] **A recurring pin appears at the configured interval.**
- [ ] **Every page and every image URL returns 401 or the login page without a session.** Test in a private window.
- [ ] **Portrait photos display upright from both iOS and Android.**
- [ ] **Killing the instance mid-event loses nothing.** Deploy a no-op revision while the slideshow is running; it should continue from the next poll.
- [ ] **`terraform destroy` leaves no bucket, service or secret behind.** Run it in a scratch project first if you want to check without losing the real one.

- [ ] **Step 4: Write the runbook**

Create `docs/RUNBOOK.md` capturing what the operator needs on the night, drawn from the spec's Operations section:

```markdown
# Event photo bot — runbook

## Before the event
- [ ] Bot created, token in Secret Manager, `/setuserpic` and description set
- [ ] Whitelist collected via pairing mode, then pairing mode turned off
- [ ] Programme and menu images uploaded and pinned as recurring
- [ ] Takeover set and cleared once, so whoever runs the screen has done it before they need to
- [ ] Full path tested from a real phone: send, queue, approve, appears on screen
- [ ] Portrait photo from both an iPhone and an Android checked for orientation
- [ ] Slideshow run for an hour on the actual display machine
- [ ] Login link and password shared with whoever will run the screen
- [ ] Display machine's browser on the slideshow, fullscreen, OS sleep disabled

## During
The admin watches the queue on a phone. If nobody can moderate, turn on
auto-approve for trusted senders and mark everyone trusted — the whitelist is
then the only control, which is reasonable for a known group.

If the screen freezes: reload the page. The manifest reloads from the next poll.

If one image is stuck on screen: the takeover banner on every admin page has a
clear button.

## After
- [ ] Download the `originals/` prefix if anyone wants the photos:
      `gcloud storage cp -r gs://BUCKET/originals ./photos`
- [ ] `deleteWebhook` on the bot, then delete the bot via BotFather
- [ ] `terraform -chdir=infra destroy`
- [ ] Confirm the bucket is gone, not just emptied
```

- [ ] **Step 5: Commit**

```bash
git add infra/deploy.ps1 docs/RUNBOOK.md
git commit -m "ops: deploy script, webhook registration and runbook"
```

---

## Self-Review

Run against the spec after the plan is written, before execution starts.

**Spec coverage.** Each section maps to a task:

| Spec section | Task |
| --- | --- |
| Architecture, decisions | 2, 3, 9 |
| Data model (`images`, `settings`, whitelist) | 2, 11 |
| Telegram bot: ingest rules, replies, failure handling | 10 |
| Web: password gate | 6 |
| Web: slideshow | 7, 12 |
| Web: approval queue | 13 |
| Web: image management, forced image | 11, 13 |
| Web: settings | 11, 13 |
| HTTP surface | 9, 11, 13 |
| Image pipeline and storage | 8 |
| Bucket configuration | 15 |
| Infrastructure as code | 15 |
| Configuration and secrets | 5, 15, 16 |
| Pairing mode / getting user ids | 10, 13 |
| Privacy notes | 10 (the `/start` reply test), 16 (teardown) |
| Operations | 16 |
| Acceptance criteria | 16 |

**Known gaps, stated rather than hidden:**

1. **Cost verification is not a task.** The spec's own note stands: check the figures against the pricing calculator for `europe-north1`, in particular that warm idle instances are not billed under `cpu_idle = true`. The architecture rests on it but no code can prove it.
2. **The `/api/images` and `/api/settings` read routes appear in Task 13** rather than Task 11, because that is where the need for them becomes visible. If you prefer them in the API task, move them — they are additive and have no dependents.
3. **No CSRF token.** `SameSite=Lax` blocks cross-site POSTs carrying the cookie, which covers the mutating routes. The spec asks for this to be stated explicitly rather than implied; it is stated here.
4. **Task 12 and 13 have no automated tests.** They are browser behaviour over a contract that is already tested. The acceptance checks in Task 16 cover them.

**Type consistency check.** `ImageRecord`, `Settings`, `EventState`, `ObjectPaths`, `StateStore.MutateAsync`, `ITelegramClient`, `ProcessedImage` and the manifest records are used with the same names and signatures in every task that consumes them. `PinKind` has exactly two members throughout; `ImageStatus` has four. `ITelegramClient` is declared in Task 9 Step 4 and implemented in Task 10 — that ordering is deliberate, because `AppFactory` needs it to compile.

**One thing to decide before Task 8:** the ImageSharp licensing question at the top of this plan.
