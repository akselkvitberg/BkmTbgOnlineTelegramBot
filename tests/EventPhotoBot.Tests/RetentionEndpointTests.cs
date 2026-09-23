using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class RetentionEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public RetentionEndpointTests(AppFactory factory) => _factory = factory;

    private HttpRequestMessage Sweep(string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/retention");
        if (secret is not null) request.Headers.Add("X-Retention-Secret", secret);
        return request;
    }

    [Fact]
    public async Task The_sweep_needs_the_secret_but_no_session()
    {
        var client = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Sweep(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Sweep("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Sweep(AppFactory.DefaultRetentionSecret))).StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_secret_the_sweep_route_does_not_exist()
    {
        using var factory = new AppFactory { RetentionSecret = null };

        var response = await factory.CreateAnonymousClient().SendAsync(Sweep("anything"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retention_is_set_per_event_and_validated()
    {
        var client = _factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = 30, keepNewest = 50 })).StatusCode);
        Assert.Equal(30, _factory.Store.Snapshot.Default().Retention.MaxAgeDays);
        Assert.Equal(50, _factory.Store.Snapshot.Default().Retention.KeepNewest);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = 0, keepNewest = 0 })).StatusCode);

        await client.PutAsJsonAsync("/api/events/daglig/retention", new { maxAgeDays = (int?)null, keepNewest = (int?)null });
        Assert.Null(_factory.Store.Snapshot.Default().Retention.MaxAgeDays);
    }

    [Fact]
    public async Task Rydd_na_runs_the_sweep_for_one_event()
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.Store.MutateAsync(s =>
        {
            s.Default().Retention = new Retention { MaxAgeDays = 1 };
            s.Images["ancient"] = new ImageRecord
            {
                Id = "ancient", EventId = "daglig", Sha256 = "a", SortKey = "a", OriginalExtension = "jpg",
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
            };
        });

        var response = await client.PostAsync("/api/events/daglig/retention/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("ancient", _factory.Store.Snapshot.Images.Keys);
    }
}
