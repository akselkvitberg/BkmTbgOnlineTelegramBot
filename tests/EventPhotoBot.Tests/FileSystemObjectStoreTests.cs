using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

/// <summary>
/// The local-dev store has to honour the same generation contract as GCS, or
/// StateStore's optimistic concurrency silently stops working on a workstation.
/// </summary>
public sealed class FileSystemObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epb-fs-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemObjectStore _store;

    public FileSystemObjectStoreTests() => _store = new FileSystemObjectStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_missing_object_reads_as_null()
    {
        Assert.Null(await _store.ReadAsync("state/state.json"));
        Assert.Null(await _store.OpenReadAsync("thumbs/x.jpg"));
    }

    [Fact]
    public async Task Each_write_advances_the_generation_even_back_to_back()
    {
        var first = await _store.WriteAsync("state/state.json", [1], "application/json", 0);
        var second = await _store.WriteAsync("state/state.json", [2], "application/json", first);

        Assert.True(second > first);
        var read = await _store.ReadAsync("state/state.json");
        Assert.Equal([2], read!.Bytes);
        Assert.Equal(second, read.Generation);
    }

    [Fact]
    public async Task A_stale_generation_is_refused()
    {
        var first = await _store.WriteAsync("state/state.json", [1], "application/json", 0);
        await _store.WriteAsync("state/state.json", [2], "application/json", first);

        await Assert.ThrowsAsync<PreconditionFailedException>(
            () => _store.WriteAsync("state/state.json", [3], "application/json", first));
        await Assert.ThrowsAsync<PreconditionFailedException>(
            () => _store.WriteAsync("state/state.json", [3], "application/json", 0));
    }

    [Fact]
    public async Task A_path_outside_the_root_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync("display/../../escape.jpg"));
    }

    [Fact]
    public async Task Delete_of_a_missing_object_is_a_no_op()
    {
        await _store.DeleteAsync("originals/nothing.jpg");
    }
}
