using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class SenderApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public SenderApiTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(long senderId, ImageStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status, SenderId = senderId,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Setting_a_status_creates_a_row_for_an_unknown_id()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5001/status", new { status = "autoApprove" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sender = Assert.Single(_factory.Store.Snapshot.Settings.Senders, s => s.Id == 5001);
        Assert.Equal(SenderStatus.AutoApprove, sender.Status);
    }

    [Fact]
    public async Task Banning_a_sender_rejects_every_image_they_sent()
    {
        var client = _factory.CreateAuthenticatedClient();
        var pending = await SeedImageAsync(5002, ImageStatus.Pending);
        var approved = await SeedImageAsync(5002, ImageStatus.Approved);

        await client.PostAsJsonAsync("/api/senders/5002/status", new { status = "banned" });

        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[pending].Status);
        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[approved].Status);
    }

    [Fact]
    public async Task Banning_the_holder_of_takeover_clears_it_in_the_same_write()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(5003, ImageStatus.Approved);
        await _factory.Store.MutateAsync(s => s.Settings.TakeoverImageId = id);

        await client.PostAsJsonAsync("/api/senders/5003/status", new { status = "banned" });

        Assert.Null(_factory.Store.Snapshot.Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Banning_a_sender_with_no_images_succeeds()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5004/status", new { status = "banned" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_status_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/senders/5005/status", new { status = "vip" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Enum.TryParse accepts a numeric string as well as a name, so without a guard
    /// {"status": "99"} wrote (SenderStatus)99 into the roster — a status the admin
    /// page cannot render and, worse on this endpoint than on the others, one that is
    /// neither Banned nor AutoApprove, so the photo pipeline treats that person as
    /// merely Known for the rest of the event. "1" is the other half: it lands on a
    /// declared value, passes Enum.IsDefined, and silently means AutoApprove — this
    /// endpoint is the one where that mistake pre-approves a stranger.
    ///
    /// Each case takes its own sender id: the roster is shared across this class.
    /// </summary>
    [Theory]
    [InlineData(5009, "99")]
    [InlineData(5010, "1")]
    [InlineData(5011, "-1")]
    public async Task A_numeric_sender_status_is_rejected_and_leaves_the_sender_alone(
        long id, string status)
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync($"/api/senders/{id}/status", new { status = "known" });

        var response = await client.PostAsJsonAsync($"/api/senders/{id}/status", new { status });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var sender = Assert.Single(_factory.Store.Snapshot.Settings.Senders, s => s.Id == id);
        Assert.Equal(SenderStatus.Known, sender.Status);
    }

    [Fact]
    public async Task Setting_a_status_needs_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/senders/5006/status", new { status = "banned" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_settings_patch_cannot_write_the_roster()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/senders/5007/status", new { status = "known" });

        await client.PatchAsJsonAsync("/api/settings", new { senders = Array.Empty<object>() });

        Assert.Contains(_factory.Store.Snapshot.Settings.Senders, s => s.Id == 5007);
    }

    [Fact]
    public async Task The_image_list_carries_the_sender_id_so_the_queue_can_ban()
    {
        var client = _factory.CreateAuthenticatedClient();
        await SeedImageAsync(5008, ImageStatus.Pending);

        var json = await client.GetStringAsync("/api/images?status=pending");

        Assert.Contains("5008", json);
    }
}
