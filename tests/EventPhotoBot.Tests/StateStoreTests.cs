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

    [Fact]
    public async Task Mutate_leaves_state_unchanged_when_retries_are_exhausted()
    {
        var inner = new InMemoryObjectStore();
        var seed = new StateStore(inner);
        await seed.LoadAsync();
        await seed.MutateAsync(s => s.Settings.SlideSeconds = 3);

        // Every write to state.json loses the race, no matter how many times it
        // retries, so retries are eventually exhausted.
        var store = new StateStore(new AlwaysConflictingObjectStore(inner));
        await store.LoadAsync();
        var generationBefore = store.Generation;

        await Assert.ThrowsAsync<PreconditionFailedException>(() =>
            store.MutateAsync(s => s.Settings.SlideSeconds = 777));

        // The attempted change is gone; Snapshot and Generation still agree with
        // each other and with what was actually last persisted.
        Assert.Equal(3, store.Snapshot.Settings.SlideSeconds);
        Assert.Equal(generationBefore, store.Generation);
    }

    [Fact]
    public async Task Load_retries_a_transient_read_failure_and_then_succeeds()
    {
        var inner = new InMemoryObjectStore();
        var seed = new StateStore(inner);
        await seed.LoadAsync();
        await seed.MutateAsync(s => s.Settings.SlideSeconds = 42);

        // The bucket read fails twice — a permission-propagation lag, a passing
        // GCS 5xx — before succeeding on the third attempt.
        var flaky = new FailingNTimesObjectStore(inner, failCount: 2);
        var store = new StateStore(flaky);

        await store.LoadAsync();

        Assert.Equal(42, store.Snapshot.Settings.SlideSeconds);
        Assert.Equal(2, flaky.FailedReads);
    }

    [Fact]
    public async Task Load_gives_up_after_the_bucket_stays_unreachable()
    {
        // Every read fails, no matter how many times it retries, simulating a
        // bucket that is genuinely broken rather than transiently slow.
        var flaky = new FailingNTimesObjectStore(new InMemoryObjectStore(), failCount: int.MaxValue);
        var store = new StateStore(flaky);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Mutate_leaves_state_unchanged_when_the_callback_throws()
    {
        var (store, _) = NewStore();
        await store.LoadAsync();
        await store.MutateAsync(s => s.Settings.SlideSeconds = 3);
        var generationBefore = store.Generation;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MutateAsync(s =>
            {
                s.Settings.SlideSeconds = 777;
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(3, store.Snapshot.Settings.SlideSeconds);
        Assert.Equal(generationBefore, store.Generation);
    }

    [Fact]
    public async Task Mutate_leaves_state_unchanged_when_cancelled_mid_write()
    {
        var inner = new InMemoryObjectStore();
        var store = new StateStore(new CancellationCheckingObjectStore(inner));
        await store.LoadAsync();
        await store.MutateAsync(s => s.Settings.SlideSeconds = 3);
        var generationBefore = store.Generation;

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.MutateAsync(s =>
            {
                s.Settings.SlideSeconds = 777;
                // Cancel once the mutation has been computed but before the write
                // that would confirm it is allowed to complete.
                cts.Cancel();
            }, cts.Token));

        Assert.Equal(3, store.Snapshot.Settings.SlideSeconds);
        Assert.Equal(generationBefore, store.Generation);
    }

    /// <summary>Test double: every write to StatePath fails the precondition, no
    /// matter what generation is offered, simulating a writer that always wins the
    /// race. Reads pass through untouched, so a reload always sees the same object.</summary>
    private sealed class AlwaysConflictingObjectStore(IObjectStore inner) : IObjectStore
    {
        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default) =>
            inner.ReadAsync(path, ct);

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
            long? ifGenerationMatch, CancellationToken ct = default) =>
            path == StateStore.StatePath
                ? throw new PreconditionFailedException(path)
                : inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
            inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            inner.DeleteAsync(path, ct);
    }

    /// <summary>Test double: the first <paramref name="failCount"/> calls to
    /// ReadAsync throw, simulating a transiently (or persistently, if failCount is
    /// large enough) unreachable bucket. Every other member passes straight
    /// through, the same way AlwaysConflictingObjectStore above wraps writes.</summary>
    private sealed class FailingNTimesObjectStore(IObjectStore inner, int failCount) : IObjectStore
    {
        private int _reads;

        /// <summary>Test hook: how many ReadAsync calls actually failed.</summary>
        public int FailedReads { get; private set; }

        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default)
        {
            _reads++;
            if (_reads <= failCount)
            {
                FailedReads++;
                throw new InvalidOperationException($"Simulated transient read failure #{_reads}.");
            }

            return inner.ReadAsync(path, ct);
        }

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
            long? ifGenerationMatch, CancellationToken ct = default) =>
            inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
            inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            inner.DeleteAsync(path, ct);
    }

    /// <summary>Test double: honours cancellation the way a real network-backed
    /// IObjectStore would, which InMemoryObjectStore does not need to.</summary>
    private sealed class CancellationCheckingObjectStore(IObjectStore inner) : IObjectStore
    {
        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return inner.ReadAsync(path, ct);
        }

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
            long? ifGenerationMatch, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);
        }

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return inner.OpenReadAsync(path, ct);
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return inner.DeleteAsync(path, ct);
        }
    }
}
