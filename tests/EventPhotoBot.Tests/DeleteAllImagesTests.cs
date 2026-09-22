using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

/// <summary>
/// A class of its own, with its own AppFactory: clearing every image would otherwise
/// reach into whatever the other admin tests had seeded into a shared fixture.
/// </summary>
public class DeleteAllImagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public DeleteAllImagesTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "png"), [1], "image/png", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "png",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Deleting_all_images_removes_every_entry_and_object_and_ends_a_takeover()
    {
        var client = _factory.CreateAuthenticatedClient();
        var ids = new[]
        {
            await SeedImageAsync(ImageStatus.Approved),
            await SeedImageAsync(ImageStatus.Pending),
            await SeedImageAsync(ImageStatus.Hidden),
            await SeedImageAsync(ImageStatus.Rejected),
        };
        await client.PutAsJsonAsync("/api/takeover", new { imageId = ids[0], minutes = (int?)null });
        var generationBefore = _factory.Store.Generation;

        var response = await client.DeleteAsync("/api/images");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeleteAllResponse>();
        Assert.Equal(ids.Length, body!.Deleted);

        // One write for the lot, so the screen never shows a half-cleared set.
        Assert.Equal(generationBefore + 1, _factory.Store.Generation);
        Assert.Empty(_factory.Store.Snapshot.Images);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverUntil);
        foreach (var id in ids)
        {
            Assert.DoesNotContain(ObjectPaths.Display(id), _factory.Objects.Paths);
            Assert.DoesNotContain(ObjectPaths.Thumb(id), _factory.Objects.Paths);
            Assert.DoesNotContain(ObjectPaths.Original(id, "png"), _factory.Objects.Paths);
        }
    }

    [Fact]
    public async Task Deleting_all_when_there_are_none_is_ok_and_does_not_write()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.DeleteAsync("/api/images");
        var generationBefore = _factory.Store.Generation;

        var response = await client.DeleteAsync("/api/images");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeleteAllResponse>();
        Assert.Equal(0, body!.Deleted);
        Assert.Equal(generationBefore, _factory.Store.Generation);
    }

    [Fact]
    public async Task Deleting_all_requires_a_session()
    {
        await SeedImageAsync(ImageStatus.Approved);

        var response = await _factory.CreateAnonymousClient().DeleteAsync("/api/images");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(_factory.Store.Snapshot.Images);
    }

    private sealed record DeleteAllResponse(int Deleted);
}
