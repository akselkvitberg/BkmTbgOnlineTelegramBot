using System.Text.Json;

namespace EventPhotoBot.State;

/// <summary>
/// Owns the single source of truth. All state lives in memory; every change
/// rewrites state/state.json with an if-generation-match precondition, so an
/// interleaved write is detected and retried rather than silently overwritten.
/// This is the only type that writes state.json.
/// </summary>
public sealed class StateStore(IObjectStore objects, ILogger<StateStore>? logger = null, StateSeed? seed = null)
{
    public const string StatePath = "state/state.json";
    public const string PrevPath = "state/state-prev.json";
    private const int MaxAttempts = 4;
    private const int LoadMaxAttempts = 3;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _loadLock = new();
    private Task? _initialLoad;
    private volatile bool _loaded;
    private EventState _state = new();
    private byte[] _lastBytes = [];
    private long _generation;

    /// <summary>
    /// The live state. Read freely; mutate only through MutateAsync. Throws until
    /// the state has been loaded: serving the empty defaults instead would show an
    /// empty slideshow and let a mutation build on nothing.
    /// </summary>
    public EventState Snapshot => _loaded ? _state : throw NotLoaded();

    /// <summary>The GCS generation of state.json, used directly as the manifest ETag.</summary>
    public long Generation => _loaded ? _generation : throw NotLoaded();

    public bool IsLoaded => _loaded;

    private static InvalidOperationException NotLoaded() =>
        new("State has not been loaded yet; call EnsureLoadedAsync first.");

    /// <summary>
    /// Whether the last read changed the state it read (a file from before events, or
    /// no file at all). InitializeAsync persists it, so a generated join code is fixed
    /// before anyone scans it.
    /// </summary>
    public bool MigratedOnLoad { get; private set; }

    /// <summary>
    /// Loads state.json the first time anyone needs it, not at startup: startup has
    /// to reach the listening port as fast as possible, and a GCS read there only
    /// delays that. Concurrent callers share one load, migration write included. The
    /// load is not tied to any one caller's cancellation, so a request that gives up
    /// does not waste it; a load that fails is not cached, and the next caller tries
    /// again.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return Task.CompletedTask;

        Task load;
        lock (_loadLock)
        {
            if (_initialLoad is null || _initialLoad.IsFaulted || _initialLoad.IsCanceled)
                _initialLoad = InitializeAsync(CancellationToken.None);
            load = _initialLoad;
        }
        return load.WaitAsync(ct);
    }

    /// <summary>
    /// The first load: read and migrate, then one write if the read migrated
    /// anything, and only then is the state served. A migration that is visible but
    /// not yet written could hand out a generated join code that a restart would
    /// replace; this way a failed write fails the load, and the next caller retries
    /// the whole thing.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ReadAndMigrateAsync(ct);
            if (MigratedOnLoad) await WriteLockedAsync<object?>(_ => null, ct);
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads and migrates in memory, without writing a migration back.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await ReadAndMigrateAsync(ct);
        _loaded = true;
    }

    private async Task ReadAndMigrateAsync(CancellationToken ct)
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

    /// <summary>
    /// The first request after a cold start waits on this, so a transient failure
    /// reaching the bucket — permission propagation lag on a fresh deploy, a passing
    /// GCS 5xx, a cold IAM token fetch — must not fail that request. A small bounded
    /// retry absorbs that; a persistently broken bucket still fails the request once
    /// attempts are exhausted, and the next request tries again. Cancellation is never
    /// retried — it means the caller stopped waiting, not that the bucket is unreachable.
    /// </summary>
    private async Task<StoredObject?> ReadStateWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await objects.ReadAsync(StatePath, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException && attempt < LoadMaxAttempts)
            {
                logger?.LogWarning(e,
                    "Failed to load state (attempt {Attempt}/{MaxAttempts}); retrying.",
                    attempt, LoadMaxAttempts);
                await Task.Delay(LoadRetryDelay, ct);
            }
        }
    }

    public async Task MutateAsync(Action<EventState> mutate, CancellationToken ct = default) =>
        await MutateAsync<object?>(s => { mutate(s); return null; }, ct);

    public async Task<T> MutateAsync<T>(Func<EventState, T> mutate, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            return await WriteLockedAsync(mutate, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The write loop. The caller holds _gate.</summary>
    private async Task<T> WriteLockedAsync<T>(Func<EventState, T> mutate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Mutate a clone, never the live _state: until WriteAsync to StatePath
            // has actually succeeded, nothing here is confirmed, and Snapshot must
            // never show a change the bucket does not have. This also means a
            // thrown mutate callback, a non-precondition write failure, or a
            // cancellation leaves _state exactly as it was before this call.
            var working = Clone(_state);
            ClearExpiredTakeover(working);
            var result = mutate(working);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(working, StateJson.Options);

            try
            {
                // Backup first: if the state write then fails, the previous
                // generation is still one object away.
                if (_lastBytes.Length > 0)
                    await objects.WriteAsync(PrevPath, _lastBytes, "application/json", null, ct);

                _generation = await objects.WriteAsync(
                    StatePath, bytes, "application/json",
                    ifGenerationMatch: _generation, ct);
                _state = working;
                _lastBytes = bytes;
                // One line per write, so the point where one file stops being
                // enough is visible in the logs before it is felt in admin.
                logger?.LogInformation("Wrote state.json at generation {Generation}: {Bytes} bytes.",
                    _generation, bytes.Length);
                return result;
            }
            catch (PreconditionFailedException) when (attempt < MaxAttempts)
            {
                logger?.LogWarning(
                    "State generation moved under us (attempt {Attempt}); reloading and reapplying.",
                    attempt);
                await ReadAndMigrateAsync(ct);
            }
        }
    }

    private static EventState Clone(EventState state) =>
        JsonSerializer.Deserialize<EventState>(
            JsonSerializer.SerializeToUtf8Bytes(state, StateJson.Options),
            StateJson.Options)!;

    /// <summary>
    /// Takeover expiry triggers no write of its own, so the client drops an expired
    /// takeover on its own clock. This converges the stored state on the next change.
    /// </summary>
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
}
