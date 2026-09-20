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
