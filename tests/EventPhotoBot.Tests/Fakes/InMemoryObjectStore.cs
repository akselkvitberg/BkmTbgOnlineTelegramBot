using EventPhotoBot.State;

namespace EventPhotoBot.Tests.Fakes;

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly Dictionary<string, StoredObject> _objects = [];

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
        var current = _objects.TryGetValue(path, out var existing) ? existing.Generation : 0L;
        if (ifGenerationMatch is { } expected && current != expected)
            throw new PreconditionFailedException(path);

        // Generation is tracked per object path, as real GCS generations are: writing
        // one object (e.g. the state-prev backup) must never advance the generation
        // reported for a different object (e.g. state.json). A single shared counter
        // across all paths would make store.Generation jump by more than one write per
        // logical MutateAsync call, which is not how the real object store behaves.
        var generation = current + 1;
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
    public void ForceWrite(string path, byte[] bytes)
    {
        var current = _objects.TryGetValue(path, out var existing) ? existing.Generation : 0L;
        _objects[path] = new StoredObject(bytes, current + 1);
    }
}
