using System.IO.Compression;
using System.Net;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ExportTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ExportTests(AppFactory factory) => _factory = factory;

    private async Task SeedAsync(string id, ImageStatus status, byte[] original, DateTimeOffset at)
    {
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), original, "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = "daglig", Sha256 = id, SortKey = id, Status = status,
            OriginalExtension = "jpg", ReceivedAt = at,
        });
    }

    [Fact]
    public async Task The_zip_holds_the_approved_originals_only()
    {
        var at = new DateTimeOffset(2026, 9, 23, 18, 5, 9, TimeSpan.Zero);
        await SeedAsync("EXP1", ImageStatus.Approved, [1, 2, 3], at);
        await SeedAsync("EXP2", ImageStatus.Pending, [4], at);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/events/daglig/export.zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var entry = Assert.Single(zip.Entries, e => e.Name.Contains("EXP"));
        Assert.Equal("20260923-180509Z-EXP1.jpg", entry.Name);
        using var content = new MemoryStream();
        await entry.Open().CopyToAsync(content);
        Assert.Equal([1, 2, 3], content.ToArray());
    }

    [Fact]
    public async Task An_unknown_event_is_404_and_a_session_is_required()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateAuthenticatedClient().GetAsync("/api/events/nope/export.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateAnonymousClient().GetAsync("/api/events/daglig/export.zip")).StatusCode);
    }
}
