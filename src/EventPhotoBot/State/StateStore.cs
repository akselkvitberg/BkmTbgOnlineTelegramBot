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
