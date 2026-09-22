namespace EventPhotoBot.State;

/// <summary>
/// IObjectStore on the local disk, for running the app on a workstation (LOCAL_DEV).
/// Not for production: one process, one directory, no durability promises.
///
/// The generation is the file's last-write time in ticks, which survives a restart
/// the way a GCS generation does. Every write stamps a value strictly greater than
/// the one before it, so two writes inside the clock's resolution still produce two
/// generations and the if-generation-match check in StateStore keeps working.
/// </summary>
public sealed class FileSystemObjectStore : IObjectStore
{
    private readonly string _root;
    private readonly Lock _lock = new();

    public FileSystemObjectStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default)
    {
        var file = Resolve(path);
        lock (_lock)
        {
            if (!File.Exists(file)) return Task.FromResult<StoredObject?>(null);
            return Task.FromResult<StoredObject?>(
                new StoredObject(File.ReadAllBytes(file), GenerationOf(file)));
        }
    }

    public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
        long? ifGenerationMatch, CancellationToken ct = default)
    {
        var file = Resolve(path);
        lock (_lock)
        {
            var exists = File.Exists(file);
            var current = exists ? GenerationOf(file) : 0;
            if (ifGenerationMatch is { } expected && expected != current)
                throw new PreconditionFailedException(path);

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Write-then-rename, so a crash mid-write never leaves a torn state.json.
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);

            var generation = Math.Max(DateTime.UtcNow.Ticks, current + 1);
            File.SetLastWriteTimeUtc(file, new DateTime(generation, DateTimeKind.Utc));
            return Task.FromResult(GenerationOf(file));
        }
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var file = Resolve(path);
        lock (_lock)
        {
            // Read fully into memory rather than handing out a FileStream: a later
            // write-then-rename over an open handle fails on Windows.
            return Task.FromResult<Stream?>(
                File.Exists(file) ? new MemoryStream(File.ReadAllBytes(file), writable: false) : null);
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var file = Resolve(path);
        // File.Delete ignores a missing file but throws for a missing directory.
        lock (_lock) if (File.Exists(file)) File.Delete(file);
        return Task.CompletedTask;
    }

    private static long GenerationOf(string file) => File.GetLastWriteTimeUtc(file).Ticks;

    /// <summary>
    /// Object names reach here from the /img/{id} route, so a name that climbs out
    /// of the root is refused rather than trusted to be a server-built path.
    /// </summary>
    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Object path '{path}' escapes the storage directory.");
        return full;
    }
}
