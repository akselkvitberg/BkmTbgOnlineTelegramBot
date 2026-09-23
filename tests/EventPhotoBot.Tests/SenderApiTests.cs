using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class SenderApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public SenderApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<string> SeedImageAsync(long senderId, ImageStatus status, string eventId = StateMigration.DefaultEventId)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = eventId, Sha256 = id, SortKey = id, Status = status, SenderId = senderId,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Pre_approving_creates_a_row_with_an_auto_approve_membership()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5001/memberships/daglig", new { autoApprove = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sender = Assert.Single(_factory.Store.Snapshot.Senders, s => s.Id == 5001);
        Assert.False(sender.Banned);
        Assert.True(sender.MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task Auto_approve_is_per_event()
    {
        await _factory.Store.MutateAsync(s => { if (s.Find("s-bryllup") is null) s.AddEvent("s-bryllup"); });

        await Client.PostAsJsonAsync("/api/senders/5012/memberships/s-bryllup", new { autoApprove = true });

        var sender = _factory.Store.Snapshot.Senders.Single(s => s.Id == 5012);
        Assert.Null(sender.MembershipIn("daglig"));
        Assert.True(sender.MembershipIn("s-bryllup")!.AutoApprove);
    }

    [Fact]
    public async Task Turning_auto_approve_off_keeps_the_membership()
    {
        await Client.PostAsJsonAsync("/api/senders/5013/memberships/daglig", new { autoApprove = true });
        await Client.PostAsJsonAsync("/api/senders/5013/memberships/daglig", new { autoApprove = false });

        Assert.False(_factory.Store.Snapshot.Senders.Single(s => s.Id == 5013).MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task A_membership_in_an_unknown_event_is_404_and_a_missing_value_is_400()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PostAsJsonAsync("/api/senders/5014/memberships/nope", new { autoApprove = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.PostAsJsonAsync("/api/senders/5014/memberships/daglig", new { })).StatusCode);
        Assert.DoesNotContain(_factory.Store.Snapshot.Senders, s => s.Id == 5014);
    }

    [Fact]
    public async Task Banning_rejects_everything_they_sent_in_every_event()
    {
        await _factory.Store.MutateAsync(s => { if (s.Find("s-bryllup") is null) s.AddEvent("s-bryllup"); });
        var daily = await SeedImageAsync(5002, ImageStatus.Approved);
        var wedding = await SeedImageAsync(5002, ImageStatus.Pending, "s-bryllup");

        await Client.PostAsJsonAsync("/api/senders/5002/ban", new { banned = true });

        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[daily].Status);
        Assert.Equal(ImageStatus.Rejected, _factory.Store.Snapshot.Images[wedding].Status);
        Assert.True(_factory.Store.Snapshot.Senders.Single(s => s.Id == 5002).Banned);
    }

    [Fact]
    public async Task Banning_the_holder_of_takeover_clears_it_in_the_same_write()
    {
        var id = await SeedImageAsync(5003, ImageStatus.Approved);
        await _factory.Store.MutateAsync(s => s.Default().Settings.TakeoverImageId = id);

        await Client.PostAsJsonAsync("/api/senders/5003/ban", new { banned = true });

        Assert.Null(_factory.Store.Snapshot.Default().Settings.TakeoverImageId);
    }

    [Fact]
    public async Task Unbanning_restores_the_memberships()
    {
        await Client.PostAsJsonAsync("/api/senders/5004/memberships/daglig", new { autoApprove = true });

        await Client.PostAsJsonAsync("/api/senders/5004/ban", new { banned = true });
        await Client.PostAsJsonAsync("/api/senders/5004/ban", new { banned = false });

        var sender = _factory.Store.Snapshot.Senders.Single(s => s.Id == 5004);
        Assert.False(sender.Banned);
        Assert.True(sender.MembershipIn("daglig")!.AutoApprove);
    }

    [Fact]
    public async Task A_ban_request_without_a_value_is_rejected()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5005/ban", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_roster_routes_need_a_session()
    {
        var anonymous = _factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/senders/5006/ban", new { banned = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/senders/5006/memberships/daglig", new { autoApprove = true })).StatusCode);
    }

    [Fact]
    public async Task The_old_status_route_is_gone()
    {
        var response = await Client.PostAsJsonAsync("/api/senders/5015/status", new { status = "banned" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_settings_patch_cannot_write_the_roster()
    {
        await Client.PostAsJsonAsync("/api/senders/5007/memberships/daglig", new { autoApprove = true });

        await Client.PatchAsJsonAsync("/api/settings", new { senders = Array.Empty<object>() });

        Assert.Contains(_factory.Store.Snapshot.Senders, s => s.Id == 5007);
    }

    [Fact]
    public async Task The_image_list_carries_the_sender_id_so_the_queue_can_ban()
    {
        await SeedImageAsync(5008, ImageStatus.Pending);

        var json = await Client.GetStringAsync("/api/images?status=pending");

        Assert.Contains("5008", json);
    }
}
