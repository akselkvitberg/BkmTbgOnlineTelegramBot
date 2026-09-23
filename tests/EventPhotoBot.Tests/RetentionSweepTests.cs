using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class RetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 3, 0, 0, TimeSpan.Zero);

    private static EventState With(int? maxAgeDays, int keepNewest,
        params (string Id, int AgeDays, ImageStatus Status, PinKind Pin)[] images)
    {
        var state = TestState.New();
        state.Default().Retention = new Retention { MaxAgeDays = maxAgeDays, KeepNewest = keepNewest };
        foreach (var (id, age, status, pin) in images)
            state.Images[id] = new ImageRecord
            {
                Id = id, EventId = "daglig", Sha256 = id, SortKey = id, OriginalExtension = "jpg",
                Status = status, Pin = pin, ReceivedAt = Now.AddDays(-age),
            };
        return state;
    }

    private static string[] Selected(EventState state) =>
        [.. RetentionSweep.Select(state, state.Default(), Now).Select(i => i.Id).Order()];

    [Fact]
    public void Off_deletes_nothing() =>
        Assert.Empty(Selected(With(null, 0, ("old", 400, ImageStatus.Approved, PinKind.None))));

    [Fact]
    public void Everything_older_than_the_limit_goes_whatever_its_status()
    {
        var state = With(30, 0,
            ("new", 1, ImageStatus.Approved, PinKind.None),
            ("oldA", 31, ImageStatus.Approved, PinKind.None),
            ("oldP", 31, ImageStatus.Pending, PinKind.None),
            ("oldR", 31, ImageStatus.Rejected, PinKind.None));

        Assert.Equal(["oldA", "oldP", "oldR"], Selected(state));
    }

    [Fact]
    public void The_newest_approved_are_kept_as_a_pool_and_only_approved_count()
    {
        var state = With(30, 2,
            ("a40", 40, ImageStatus.Approved, PinKind.None),
            ("a35", 35, ImageStatus.Approved, PinKind.None),
            ("p33", 33, ImageStatus.Pending, PinKind.None),
            ("a50", 50, ImageStatus.Approved, PinKind.None));

        // a35 and a40 are the two newest approved; the pending one does not count toward K.
        Assert.Equal(["a50", "p33"], Selected(state));
    }

    [Fact]
    public void Pinned_and_takeover_photos_are_never_swept()
    {
        var state = With(30, 0,
            ("pin", 90, ImageStatus.Approved, PinKind.Recurring),
            ("held", 90, ImageStatus.Approved, PinKind.None),
            ("gone", 90, ImageStatus.Approved, PinKind.None));
        state.Default().Settings.TakeoverImageId = "held";

        Assert.Equal(["gone"], Selected(state));
    }

    [Fact]
    public void Another_events_photos_are_not_touched()
    {
        var state = With(30, 0);
        state.AddEvent("bryllup");
        state.Images["w"] = new ImageRecord
        {
            Id = "w", EventId = "bryllup", Sha256 = "w", SortKey = "w", OriginalExtension = "jpg",
            ReceivedAt = Now.AddDays(-400),
        };

        Assert.Empty(Selected(state));
    }

    [Fact]
    public async Task Running_writes_state_once_then_deletes_the_objects()
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(s =>
        {
            s.Default().Retention = new Retention { MaxAgeDays = 30 };
            s.Images["old"] = new ImageRecord
            {
                Id = "old", EventId = "daglig", Sha256 = "o", SortKey = "o", OriginalExtension = "jpg",
                ReceivedAt = Now.AddDays(-60),
            };
        });
        await objects.WriteAsync(ObjectPaths.Original("old", "jpg"), [1], "image/jpeg", null);
        var generation = store.Generation;

        var counts = await RetentionSweep.RunAsync(store, objects, NullLogger.Instance, null, Now, default);

        Assert.Equal(1, counts["daglig"]);
        Assert.Equal(generation + 1, store.Generation);
        Assert.Empty(store.Snapshot.Images);
        Assert.DoesNotContain(ObjectPaths.Original("old", "jpg"), objects.Paths);
    }

    [Fact]
    public async Task Running_with_nothing_to_do_does_not_write()
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(s => s.Default().Retention = new Retention { MaxAgeDays = 30 });
        var generation = store.Generation;

        await RetentionSweep.RunAsync(store, objects, NullLogger.Instance, null, Now, default);

        Assert.Equal(generation, store.Generation);
    }

    /// <summary>Wraps an InMemoryObjectStore and throws instead of deleting one exact path.</summary>
    private sealed class FailingDeleteObjectStore(InMemoryObjectStore inner, string failingPath) : IObjectStore
    {
        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default) => inner.ReadAsync(path, ct);

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType, long? ifGenerationMatch,
            CancellationToken ct = default) => inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            path == failingPath ? throw new InvalidOperationException($"Simulated failure deleting {path}.") : inner.DeleteAsync(path, ct);
    }

    [Fact]
    public async Task One_images_delete_failure_does_not_stop_the_rest()
    {
        var inner = new InMemoryObjectStore();
        var objects = new FailingDeleteObjectStore(inner, ObjectPaths.Display("bad"));
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(s =>
        {
            s.Default().Retention = new Retention { MaxAgeDays = 30 };
            s.Images["bad"] = new ImageRecord
            {
                Id = "bad", EventId = "daglig", Sha256 = "b", SortKey = "b", OriginalExtension = "jpg",
                ReceivedAt = Now.AddDays(-60),
            };
            s.Images["good"] = new ImageRecord
            {
                Id = "good", EventId = "daglig", Sha256 = "g", SortKey = "g", OriginalExtension = "jpg",
                ReceivedAt = Now.AddDays(-60),
            };
        });
        await inner.WriteAsync(ObjectPaths.Original("bad", "jpg"), [1], "image/jpeg", null);
        await inner.WriteAsync(ObjectPaths.Original("good", "jpg"), [1], "image/jpeg", null);

        var counts = await RetentionSweep.RunAsync(store, objects, NullLogger.Instance, null, Now, default);

        // The sweep does not throw, both records leave state, and the failure on "bad" -
        // whose Display delete is the one that throws - does not stop "good" from having
        // every one of its objects deleted.
        Assert.Equal(2, counts["daglig"]);
        Assert.Empty(store.Snapshot.Images);
        Assert.DoesNotContain(ObjectPaths.Original("good", "jpg"), inner.Paths);
        Assert.Contains(ObjectPaths.Original("bad", "jpg"), inner.Paths);
    }
}
