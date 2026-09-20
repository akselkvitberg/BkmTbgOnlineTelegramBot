using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

/// <summary>
/// Takeover is a settings field rather than a per-image flag precisely so that
/// "only one image holds the screen" is representable. These tests are what keep
/// that true as the delete and hide paths evolve.
/// </summary>
public class TakeoverInvariantTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public TakeoverInvariantTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status = ImageStatus.Approved)
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
    public async Task Setting_takeover_on_a_second_image_replaces_the_first()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await SeedImageAsync();
        var second = await SeedImageAsync();

        await client.PutAsJsonAsync("/api/takeover", new { imageId = first, minutes = (int?)null });
        await client.PutAsJsonAsync("/api/takeover", new { imageId = second, minutes = (int?)null });

        Assert.Equal(second, _factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Setting_takeover_on_a_pending_image_approves_it_in_the_same_write()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(ImageStatus.Pending);

        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 5 });

        Assert.Equal(id, _factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Equal(ImageStatus.Approved, _factory.Store.Snapshot.Images[id].Status);
    }

    [Fact]
    public async Task Deleting_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.DeleteAsync($"/api/images/{id}");

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task Hiding_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "hidden" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Rejecting_the_takeover_image_clears_the_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = (int?)null });

        await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "rejected" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Clearing_takeover_nulls_both_fields()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 60 });

        await client.DeleteAsync("/api/takeover");

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task A_duration_sets_an_expiry_and_no_duration_means_until_cleared()
    {
        var client = _factory.CreateAuthenticatedClient();
        var timed = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = timed, minutes = 15 });
        var until = _factory.Store.Snapshot.Settings.TakeoverUntil;

        Assert.NotNull(until);
        Assert.InRange(until!.Value,
            DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        var openEnded = await SeedImageAsync();
        await client.PutAsJsonAsync("/api/takeover", new { imageId = openEnded, minutes = (int?)null });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
    }

    [Fact]
    public async Task Takeover_on_an_unknown_image_is_rejected()
    {
        var response = await _factory.CreateAuthenticatedClient()
            .PutAsJsonAsync("/api/takeover", new { imageId = "nope", minutes = (int?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pin_no_longer_accepts_takeover_as_a_value()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/pin", new { pin = "takeover" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
