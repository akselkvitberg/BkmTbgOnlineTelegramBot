using System.Net;
using System.Net.Http.Json;

namespace EventPhotoBot.Tests;

/// <summary>
/// HTTP-level coverage for the webhook route itself — header checking and
/// reachability without a session. Behaviour inside a well-formed, correctly
/// authenticated update is covered by UpdateHandlerTests.
/// </summary>
public class WebhookEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public WebhookEndpointTests(AppFactory factory) => _factory = factory;

    private static object UpdateBody(long senderId, string text) => new
    {
        update_id = 1,
        message = new
        {
            message_id = 1,
            from = new { id = senderId, first_name = "Someone" },
            chat = new { id = senderId },
            text,
        },
    };

    [Fact]
    public async Task The_webhook_rejects_a_request_with_a_bad_secret_token()
    {
        var client = _factory.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/tg/{AppFactory.WebhookPath}")
        {
            Content = JsonContent.Create(UpdateBody(1, "hello")),
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "wrong-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_webhook_rejects_a_request_with_no_secret_token_header()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/tg/{AppFactory.WebhookPath}", UpdateBody(1, "hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_webhook_accepts_a_correctly_authenticated_update_without_a_session()
    {
        var client = _factory.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/tg/{AppFactory.WebhookPath}")
        {
            Content = JsonContent.Create(UpdateBody(424242, "hello")),
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", AppFactory.WebhookSecret);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
