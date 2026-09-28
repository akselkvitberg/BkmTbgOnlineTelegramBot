using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

/// <summary>Its own factory: these tests add events and clear whole events' images.</summary>
public class ScopedApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ScopedApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task EnsureEventAsync(string id, bool closed = false) =>
        await _factory.Store.MutateAsync(s => { if (s.Find(id) is null) s.AddEvent(id, name: id.ToUpperInvariant(), closed: closed); });

    private async Task<string> SeedAsync(string eventId, ImageStatus status = ImageStatus.Approved)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = eventId, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg", ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private static IEnumerable<string?> Ids(JsonElement list) =>
        list.EnumerateArray().Select(e => e.GetProperty("id").GetString());

    [Fact]
    public async Task The_image_list_filters_by_event_and_lists_every_event_without_one()
    {
        await EnsureEventAsync("a1");
        var daily = await SeedAsync("daglig");
        var special = await SeedAsync("a1");

        var scoped = await Client.GetFromJsonAsync<JsonElement>("/api/images?event=a1");
        var all = await Client.GetFromJsonAsync<JsonElement>("/api/images");

        Assert.Contains(special, Ids(scoped));
        Assert.DoesNotContain(daily, Ids(scoped));
        Assert.Contains(daily, Ids(all));
        Assert.Contains(special, Ids(all));
        Assert.Equal("a1", scoped.EnumerateArray().First().GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task Settings_are_per_event()
    {
        await EnsureEventAsync("a2");

        await Client.PatchAsJsonAsync("/api/settings?event=a2", new { slideSeconds = 33, eventName = "Konsert" });

        Assert.Equal(33, _factory.Store.Snapshot.Find("a2")!.Settings.SlideSeconds);
        Assert.Equal("Konsert", _factory.Store.Snapshot.Find("a2")!.Name);
        Assert.NotEqual(33, _factory.Store.Snapshot.Default().Settings.SlideSeconds);
        var read = await Client.GetFromJsonAsync<JsonElement>("/api/settings?event=a2");
        Assert.Equal("a2", read.GetProperty("eventId").GetString());
        Assert.Equal(33, read.GetProperty("slideSeconds").GetInt32());
    }

    [Fact]
    public async Task Takeover_lands_on_the_images_own_event()
    {
        await EnsureEventAsync("a3");
        var image = await SeedAsync("a3");

        await Client.PutAsJsonAsync("/api/takeover", new { imageId = image, minutes = (int?)null });

        Assert.Equal(image, _factory.Store.Snapshot.Find("a3")!.Settings.TakeoverImageId);
        Assert.NotEqual(image, _factory.Store.Snapshot.Default().Settings.TakeoverImageId);

        await Client.DeleteAsync("/api/takeover?event=a3");
        Assert.Null(_factory.Store.Snapshot.Find("a3")!.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Delete_all_clears_only_the_named_event()
    {
        await EnsureEventAsync("a4");
        var keep = await SeedAsync("daglig");
        var gone = await SeedAsync("a4");

        await Client.DeleteAsync("/api/images?event=a4");

        Assert.Contains(keep, _factory.Store.Snapshot.Images.Keys);
        Assert.DoesNotContain(gone, _factory.Store.Snapshot.Images.Keys);
    }

    [Fact]
    public async Task An_upload_goes_to_the_named_event()
    {
        await EnsureEventAsync("a5");
        using var form = new MultipartFormDataContent();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));
        form.Add(new ByteArrayContent(bytes), "file", "photo.jpg");

        var response = await Client.PostAsync("/api/images?event=a5", form);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal("a5", _factory.Store.Snapshot.Images[id].EventId);
    }

    [Fact]
    public async Task The_manifest_is_per_event_and_its_etag_names_the_event()
    {
        await EnsureEventAsync("a6");
        var special = await SeedAsync("a6");

        var dailyResponse = await Client.GetAsync("/api/manifest");
        var specialResponse = await Client.GetAsync("/api/manifest?event=a6");
        var manifest = await specialResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(dailyResponse.Headers.ETag!.Tag, specialResponse.Headers.ETag!.Tag);
        Assert.Contains(special, Ids(manifest.GetProperty("images")));
        Assert.Equal("A6", manifest.GetProperty("settings").GetProperty("eventName").GetString());
    }

    [Fact]
    public async Task An_event_closing_on_its_clock_changes_the_etag_without_a_write()
    {
        // Review focus 2: nothing writes at ClosesAt, so the generation stays put.
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.Find("a7") ?? s.AddEvent("a7");
            ev.ClosesAt = DateTimeOffset.UtcNow.AddSeconds(1);
        });
        var open = await Client.GetAsync("/api/manifest?event=a7");
        var generation = _factory.Store.Generation;

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest?event=a7");
        request.Headers.TryAddWithoutValidation("If-None-Match", open.Headers.ETag!.ToString());
        var closed = await Client.SendAsync(request);

        Assert.Equal(generation, _factory.Store.Generation);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var settings = (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settings");
        Assert.False(settings.GetProperty("showJoinInvite").GetBoolean());
    }

    [Fact]
    public async Task The_qr_is_refused_for_a_closed_event_and_served_for_a_scheduled_one()
    {
        await EnsureEventAsync("a8", closed: true);
        await _factory.Store.MutateAsync(s =>
        {
            var ev = s.Find("a9") ?? s.AddEvent("a9");
            ev.OpensAt = DateTimeOffset.UtcNow.AddDays(1);
        });

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/join-qr.svg?event=a8")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/join-qr.svg?event=a9")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/images?event=nope")]
    [InlineData("GET", "/api/settings?event=nope")]
    [InlineData("GET", "/api/manifest?event=nope")]
    [InlineData("DELETE", "/api/images?event=nope")]
    [InlineData("DELETE", "/api/takeover?event=nope")]
    public async Task An_unknown_event_is_404(string method, string path)
    {
        var response = await Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_event_name_is_rejected_and_the_name_kept()
    {
        await EnsureEventAsync("a10");

        var response = await Client.PatchAsJsonAsync("/api/settings?event=a10", new { eventName = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A10", _factory.Store.Snapshot.Find("a10")!.Name);
    }
}
