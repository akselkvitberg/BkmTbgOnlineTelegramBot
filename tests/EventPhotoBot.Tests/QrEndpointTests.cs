using System.Net;
using EventPhotoBot.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace EventPhotoBot.Tests;

public class QrEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public QrEndpointTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task The_qr_needs_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_qr_is_served_as_svg_when_the_username_is_known()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_qr_is_absent_when_the_username_is_unknown()
    {
        // A separate factory, so clearing the identity cannot leak into other tests.
        using var factory = new AppFactory();
        var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<BotIdentity>().Forget();

        var response = await client.GetAsync("/api/join-qr.svg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
