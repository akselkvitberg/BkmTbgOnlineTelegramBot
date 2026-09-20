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
    private const int LoadMaxAttempts = 3;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMilliseconds(250);

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
        var stored = await ReadStateWithRetryAsync(ct);
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

    /// <summary>
    /// Startup calls this before anything else is mapped, including /healthz, so a
    /// transient failure reaching the bucket — permission propagation lag on a fresh
    /// deploy, a passing GCS 5xx, a cold IAM token fetch — must not crash the revision
    /// before Kestrel ever binds. A small bounded retry absorbs that; a persistently
    /// broken bucket still fails fast once attempts are exhausted, unchanged from
    /// before. Cancellation is never retried — it means the caller stopped waiting,
    /// not that the bucket is unreachable.
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
        await _gate.WaitAsync(ct);
        try
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
        var s = state.Settings;
        if (s.TakeoverImageId is null) return;
        if (s.TakeoverUntil is { } until && until <= DateTimeOffset.UtcNow)
        {
            s.TakeoverImageId = null;
            s.TakeoverUntil = null;
        }
    }
}
