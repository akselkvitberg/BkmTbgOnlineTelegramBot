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
