using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class EventApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public EventApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateAuthenticatedClient();
    private Event? Find(string id) => _factory.Store.Snapshot.Find(id);

    [Fact]
    public void The_configured_join_code_becomes_the_default_events_code() =>
        Assert.Equal(AppFactory.JoinCode, _factory.Store.Snapshot.Default().JoinCode);

    [Fact]
    public async Task Creating_an_event_gives_it_a_fresh_code_and_default_settings()
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "bryllup-1", name = "  Bryllup  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = Find("bryllup-1")!;
        Assert.Equal("Bryllup", ev.Name);
        Assert.False(ev.IsDefault);
        Assert.NotEqual(_factory.Store.Snapshot.Default().JoinCode, ev.JoinCode);
        Assert.Null(ev.Retention.MaxAgeDays);
    }

    [Theory]
    [InlineData("Bryllup")]      // upper case
    [InlineData("bryllup 2")]    // space
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // 33 characters
    public async Task An_invalid_slug_is_rejected(string id)
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id, name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_slug_is_a_conflict()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "dup", name = "A" });
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "dup", name = "B" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("A", Find("dup")!.Name);
    }

    [Fact]
    public async Task An_empty_name_is_rejected()
    {
        var response = await Client.PostAsJsonAsync("/api/events", new { id = "noname", name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Find("noname"));
    }

    [Fact]
    public async Task Opening_after_closing_is_rejected()
    {
        var at = DateTimeOffset.UtcNow;
        var response = await Client.PostAsJsonAsync("/api/events",
            new { id = "backwards", name = "X", opensAt = at.AddHours(2), closesAt = at.AddHours(1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_list_puts_the_default_first_and_names_each_phase()
    {
        await Client.PostAsJsonAsync("/api/events",
            new { id = "later", name = "Senere", opensAt = DateTimeOffset.UtcNow.AddDays(1) });

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/events");

        var first = list.EnumerateArray().First();
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal("open", first.GetProperty("phase").GetString());
        var later = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "later");
        Assert.Equal("scheduled", later.GetProperty("phase").GetString());
        Assert.True(later.TryGetProperty("joinUrl", out _));
    }

    [Fact]
    public async Task Close_and_reopen()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "konsert", name = "Konsert" });

        await Client.PostAsync("/api/events/konsert/close", null);
        Assert.False(Find("konsert")!.IsOpen(DateTimeOffset.UtcNow));

        await Client.PostAsync("/api/events/konsert/reopen", null);
        Assert.True(Find("konsert")!.IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Reopening_an_event_whose_end_time_has_passed_clears_the_end_time()
    {
        // Review focus 5: without this the event stays closed by its clock after a reopen.
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.AddEvent("utgatt");
            ev.ClosesAt = DateTimeOffset.UtcNow.AddHours(-1);
        });

        await Client.PostAsync("/api/events/utgatt/reopen", null);

        Assert.Null(Find("utgatt")!.ClosesAt);
        Assert.True(Find("utgatt")!.IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_schedule_can_be_set_and_cleared()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "planlagt", name = "P" });
        var opens = DateTimeOffset.UtcNow.AddDays(1);

        await Client.PutAsJsonAsync("/api/events/planlagt/schedule", new { opensAt = opens, closesAt = (DateTimeOffset?)null });
        Assert.Equal(opens, Find("planlagt")!.OpensAt);

        await Client.PutAsJsonAsync("/api/events/planlagt/schedule", new { opensAt = (DateTimeOffset?)null, closesAt = (DateTimeOffset?)null });
        Assert.Null(Find("planlagt")!.OpensAt);
    }

    [Fact]
    public async Task Renaming_keeps_the_rules_for_names()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "navn", name = "Før" });

        Assert.Equal(HttpStatusCode.OK, (await Client.PatchAsJsonAsync("/api/events/navn", new { name = "Etter" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync("/api/events/navn", new { name = "" })).StatusCode);
        Assert.Equal("Etter", Find("navn")!.Name);
    }

    [Fact]
    public async Task Rotating_the_code_replaces_it()
    {
        await Client.PostAsJsonAsync("/api/events", new { id = "roter", name = "R" });
        var before = Find("roter")!.JoinCode;

        await Client.PostAsync("/api/events/roter/rotate-code", null);

        Assert.NotEqual(before, Find("roter")!.JoinCode);
    }

    [Theory]
    [InlineData("POST", "/api/events/daglig/close")]
    [InlineData("DELETE", "/api/events/daglig")]
    [InlineData("PUT", "/api/events/daglig/schedule")]
    public async Task The_default_event_cannot_be_closed_deleted_or_scheduled(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { opensAt = DateTimeOffset.UtcNow.AddDays(1) }) : null,
        };

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(_factory.Store.Snapshot.Default().IsOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Deleting_an_event_removes_its_photos_memberships_and_routes()
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s =>
        {
            s.AddEvent("slett");
            s.Images[id] = new ImageRecord
            {
                Id = id, EventId = "slett", Sha256 = id, SortKey = id, OriginalExtension = "jpg",
            };
            s.Senders.Add(new Sender
            {
                Id = 7001, CurrentEventId = "slett",
                Memberships = [new Membership { EventId = "slett" }, new Membership { EventId = "daglig" }],
            });
            s.Groups.Add(new BotGroup { Id = -7001, EventId = "slett" });
        });

        var response = await Client.DeleteAsync("/api/events/slett");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = _factory.Store.Snapshot;
        Assert.Null(state.Find("slett"));
        Assert.DoesNotContain(id, state.Images.Keys);
        Assert.DoesNotContain(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
        var sender = state.Senders.Single(s => s.Id == 7001);
        Assert.Null(sender.CurrentEventId);
        Assert.Equal(["daglig"], sender.Memberships.Select(m => m.EventId));
        Assert.Null(state.Groups.Single(g => g.Id == -7001).EventId);
    }

    [Fact]
    public async Task An_unknown_event_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/events/nope/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync("/api/events/nope")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_event_finishes_even_when_one_objects_delete_fails()
    {
        // C1: the state write already removed every record by the time the object
        // deletes run, so one failing GCS call must not stop the rest of a large
        // event's bytes from being cleaned up, or turn the response into a 500 —
        // see ImageObjects.DeleteAllAsync. A standalone factory, not the shared
        // fixture: only this test needs an IObjectStore that fails on demand.
        var badId = Guid.NewGuid().ToString("N");
        var goodId = Guid.NewGuid().ToString("N");
        using var factory = new AppFactory
        {
            ObjectStoreOverride = inner => new FailingDeleteObjectStore(inner, ObjectPaths.Display(badId)),
        };
        foreach (var id in new[] { badId, goodId })
        {
            await factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
            await factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
            await factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        }
        await factory.Store.MutateAsync(s =>
        {
            s.AddEvent("failtest");
            foreach (var id in new[] { badId, goodId })
                s.Images[id] = new ImageRecord
                    { Id = id, EventId = "failtest", Sha256 = id, SortKey = id, OriginalExtension = "jpg" };
        });

        var response = await factory.CreateAuthenticatedClient().DeleteAsync("/api/events/failtest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Store.Snapshot.Find("failtest"));
        Assert.Empty(factory.Store.Snapshot.Images);
        // The good image's objects are all gone. The bad one's Display delete throws
        // first in DeleteAsync's own sequence, so its Thumb and Original are never
        // even attempted — that one image is left exactly as it was, which is the
        // point: the *other* image in the batch is what must not be affected by it.
        Assert.Contains(ObjectPaths.Display(badId), factory.Objects.Paths);
        Assert.Contains(ObjectPaths.Thumb(badId), factory.Objects.Paths);
        Assert.Contains(ObjectPaths.Original(badId, "jpg"), factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Display(goodId), factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Thumb(goodId), factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Original(goodId, "jpg"), factory.Objects.Paths);
    }

    /// <summary>Wraps an InMemoryObjectStore and throws instead of deleting one exact path.</summary>
    private sealed class FailingDeleteObjectStore(InMemoryObjectStore inner, string failingPath) : IObjectStore
    {
        public Task<StoredObject?> ReadAsync(string path, CancellationToken ct = default) => inner.ReadAsync(path, ct);

        public Task<long> WriteAsync(string path, byte[] bytes, string contentType, long? ifGenerationMatch,
            CancellationToken ct = default) => inner.WriteAsync(path, bytes, contentType, ifGenerationMatch, ct);

        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            path == failingPath
                ? throw new InvalidOperationException($"Simulated failure deleting {path}.")
                : inner.DeleteAsync(path, ct);
    }
}
