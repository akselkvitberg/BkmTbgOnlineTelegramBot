using System.Net;

namespace EventPhotoBot.Tests;

/// <summary>
/// Behind Cloud Run's front end, Connection.RemoteIpAddress is the proxy, not the
/// caller — without ForwardedHeadersMiddleware wired ahead of the rate limiter, the
/// login limiter's "per IP" partition silently collapses into one shared bucket.
/// This passes on the naive Connection.RemoteIpAddress partition too (both callers
/// share the loopback test-host address), so it only proves anything once forwarded
/// headers are applied before the limiter runs.
/// </summary>
public class RateLimiterForwardedHeadersTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public RateLimiterForwardedHeadersTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Rate_limit_partitions_by_forwarded_ip_not_shared_bucket()
    {
        // Two distinct "clients" behind the same test-host proxy connection,
        // distinguished only by X-Forwarded-For. If forwarded headers are not
        // wired, both hit the very first (proxy) RemoteIpAddress and share one
        // limiter bucket, causing the second client to get rate-limited early.
        //
        // Every request here uses a wrong password, so every response is a 302 —
        // the rate limiter's own OnRejected redirects to /login?error=rate (see
        // AuthEndpoints), same status code as the ordinary "wrong password" redirect
        // to /login?error=bad. The Location header, not the status code, is what
        // distinguishes "still allowed through, password rejected" from "rate limited".
        async Task<string?> Hit(string ip)
        {
            var client = _factory.CreateAnonymousClient();
            var req = new HttpRequestMessage(HttpMethod.Post, "/login")
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("password", "wrong")]),
            };
            req.Headers.Add("X-Forwarded-For", ip);
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            return resp.Headers.Location?.ToString();
        }

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal("/login?error=bad", await Hit("10.0.0.1"));
        }
        // The 9th request from this same forwarded IP should now be limited.
        Assert.Equal("/login?error=rate", await Hit("10.0.0.1"));

        // A different forwarded IP must not be affected by IP 10.0.0.1's limit.
        Assert.Equal("/login?error=bad", await Hit("10.0.0.2"));
    }
}
