using System.Net;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

/// <summary>
/// Cloud Run counts an instance as started once its port accepts connections, so
/// anything done before listening is added to every cold start. Startup does no
/// I/O at all; state and the bot username are fetched by the first request that
/// needs them.
/// </summary>
public class LazyStartupTests
{
    [Fact]
    public async Task Starting_up_reads_nothing_from_the_bucket_and_asks_telegram_nothing()
    {
        using var factory = new AppFactory();
        var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, factory.Objects.ReadCount);
        Assert.Equal(0, factory.Telegram.GetMeCalls);
        Assert.False(factory.Store.IsLoaded);
    }

    [Fact]
    public async Task The_first_request_that_needs_state_loads_it_once()
    {
        using var factory = new AppFactory();
        var client = factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/settings")).StatusCode);

        Assert.True(factory.Store.IsLoaded);
        Assert.Equal(1, factory.Objects.ReadCount);
    }

    [Fact]
    public async Task The_manifest_etag_changes_when_the_join_link_becomes_known()
    {
        // Same generation, join link known or not: a screen that polled before the
        // username resolved must not be told 304 once it has.
        using var known = new AppFactory();
        using var unknown = new AppFactory();
        unknown.Telegram.Username = null;

        var withLink = (await known.CreateAuthenticatedClient().GetAsync("/api/manifest")).Headers.ETag!.Tag;
        var withoutLink = (await unknown.CreateAuthenticatedClient().GetAsync("/api/manifest")).Headers.ETag!.Tag;

        Assert.NotEqual(withLink, withoutLink);
    }
}

public class StateStoreLazyLoadTests
{
    [Fact]
    public void Snapshot_refuses_to_serve_defaults_before_a_load()
    {
        var store = new StateStore(new InMemoryObjectStore());

        Assert.Throws<InvalidOperationException>(() => store.Snapshot);
        Assert.Throws<InvalidOperationException>(() => store.Generation);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_read()
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.EnsureLoadedAsync()));

        Assert.Equal(1, objects.ReadCount);
        Assert.True(store.IsLoaded);
    }

    [Fact]
    public async Task Mutate_loads_first_rather_than_building_on_defaults()
    {
        var objects = new InMemoryObjectStore();
        var seed = new StateStore(objects);
        await seed.LoadAsync();
        await seed.MutateAsync(s => s.Default().Settings.SlideSeconds = 42);

        var store = new StateStore(objects);
        await store.MutateAsync(s => s.Default().Settings.RecurringEvery = 7);

        Assert.Equal(42, store.Snapshot.Default().Settings.SlideSeconds);
        Assert.Equal(7, store.Snapshot.Default().Settings.RecurringEvery);
    }

    [Fact]
    public async Task A_failed_load_is_not_cached_and_the_next_caller_retries()
    {
        var objects = new FlakyObjectStore { FailuresLeft = 3 };
        var store = new StateStore(objects);

        await Assert.ThrowsAnyAsync<Exception>(() => store.EnsureLoadedAsync());
        await store.EnsureLoadedAsync();

        Assert.True(store.IsLoaded);
    }

    [Fact]
    public async Task The_first_load_writes_the_migration_of_a_pre_events_file()
    {
        var objects = new InMemoryObjectStore();
        objects.ForceWrite(StateStore.StatePath,
            """{"images":{},"settings":{"eventName":"Sommerfest"}}"""u8.ToArray());
        var store = new StateStore(objects, seed: new StateSeed("party2026"));

        await store.EnsureLoadedAsync();

        var written = (await objects.ReadAsync(StateStore.StatePath))!.Bytes;
        using var document = System.Text.Json.JsonDocument.Parse(written);
        Assert.True(document.RootElement.TryGetProperty("events", out _));
        Assert.Equal("party2026", store.Snapshot.Default().JoinCode);
    }

    [Fact]
    public async Task A_migration_that_cannot_be_written_is_not_served_and_the_next_caller_retries()
    {
        // A fresh bucket migrates to a generated join code. Serving it before it is
        // written would put a code on the screen that a restart then replaces.
        var objects = new FailingWriteObjectStore { FailuresLeft = 1 };
        var store = new StateStore(objects);

        await Assert.ThrowsAnyAsync<Exception>(() => store.EnsureLoadedAsync());
        Assert.False(store.IsLoaded);

        await store.EnsureLoadedAsync();

        Assert.True(store.IsLoaded);
        Assert.NotNull(await objects.ReadAsync(StateStore.StatePath));
    }

    private sealed class FailingWriteObjectStore : IObjectStore
    {
        private readonly InMemoryObjectStore _inner = new();
        public int FailuresLeft;

        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default) =>
            _inner.ReadAsync(path, ct);

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
            long? ifGenerationMatch, CancellationToken ct = default) =>
            FailuresLeft-- > 0
                ? throw new IOException("bucket unreachable")
                : _inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
            _inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            _inner.DeleteAsync(path, ct);
    }

    private sealed class FlakyObjectStore : IObjectStore
    {
        private readonly InMemoryObjectStore _inner = new();
        public int FailuresLeft;

        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default) =>
            FailuresLeft-- > 0 ? throw new IOException("bucket unreachable") : _inner.ReadAsync(path, ct);

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType,
            long? ifGenerationMatch, CancellationToken ct = default) =>
            _inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
            _inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            _inner.DeleteAsync(path, ct);
    }
}
