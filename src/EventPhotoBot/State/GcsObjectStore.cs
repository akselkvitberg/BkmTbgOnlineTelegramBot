using Google;
using Google.Cloud.Storage.V1;
using System.Net;

namespace EventPhotoBot.State;

/// <summary>
/// IObjectStore backed by Google Cloud Storage. Translates GCS's 412 responses
/// into PreconditionFailedException and "not found" into null, per IObjectStore's
/// contract. No retry policy here: StateStore owns reload-and-retry.
/// </summary>
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
