using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class AdminApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public AdminApiTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status = ImageStatus.Pending)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Approving_an_image_sets_status_and_stamps_the_decision_time()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images[id];
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task An_unknown_status_value_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "banana" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setting_a_recurring_pin_works()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(ImageStatus.Approved);

        await client.PostAsJsonAsync($"/api/images/{id}/pin", new { pin = "recurring" });

        Assert.Equal(PinKind.Recurring, _factory.Store.Snapshot.Images[id].Pin);
    }

    [Fact]
    public async Task Deleting_an_image_removes_the_entry_and_every_object()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        await client.DeleteAsync($"/api/images/{id}");

        Assert.DoesNotContain(id, _factory.Store.Snapshot.Images.Keys);
        Assert.DoesNotContain(ObjectPaths.Display(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Thumb(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Deleting_an_unknown_image_returns_404()
    {
        var response = await _factory.CreateAuthenticatedClient().DeleteAsync("/api/images/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_upload_arrives_approved_with_all_three_objects()
    {
        var client = _factory.CreateAuthenticatedClient();
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "programme.jpg");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images.Values
            .Single(i => i.Source == ImageSource.Admin);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.Null(image.SenderId);
        Assert.Contains(ObjectPaths.Display(image.Id), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Uploading_something_that_is_not_an_image_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "notes.txt");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Settings_can_be_patched_one_field_at_a_time()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 12 });
        await client.PatchAsJsonAsync("/api/settings", new { order = "newest-first" });

        var settings = _factory.Store.Snapshot.Settings;
        Assert.Equal(12, settings.SlideSeconds);
        Assert.Equal(SlideOrder.NewestFirst, settings.Order);
        Assert.Equal(800, settings.TransitionMs); // untouched fields survive
    }

    [Fact]
    public async Task The_whitelist_is_editable_and_takes_effect_without_a_restart()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new
        {
            whitelist = new[] { new { id = 555L, name = "Late Guest", trusted = true } },
        });

        var entry = Assert.Single(_factory.Store.Snapshot.Settings.Whitelist);
        Assert.Equal(555L, entry.Id);
        Assert.True(entry.Trusted);
    }

    [Fact]
    public async Task Pairing_mode_can_be_toggled_and_seen_senders_cleared()
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.Store.MutateAsync(s =>
            s.Settings.SeenSenders.Add(new SeenSender { Id = 1, Name = "Someone" }));

        await client.PatchAsJsonAsync("/api/settings", new { pairingMode = true });
        Assert.True(_factory.Store.Snapshot.Settings.PairingMode);

        await client.PatchAsJsonAsync("/api/settings", new { clearSeenSenders = true });
        Assert.Empty(_factory.Store.Snapshot.Settings.SeenSenders);
    }

    [Fact]
    public async Task A_settings_change_advances_the_manifest_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var before = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 7 });

        var after = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Every_mutating_route_requires_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/images/anything/status", new { status = "approved" }),
            await client.PostAsJsonAsync("/api/images/anything/pin", new { pin = "recurring" }),
            await client.PutAsJsonAsync("/api/takeover", new { imageId = "anything", minutes = (int?)null }),
            await client.DeleteAsync("/api/takeover"),
            await client.DeleteAsync("/api/images/anything"),
            await client.PostAsync("/api/images", new MultipartFormDataContent()),
            await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 10 }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
