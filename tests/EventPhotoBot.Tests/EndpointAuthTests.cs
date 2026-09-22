using System.Net;
using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class EndpointAuthTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public EndpointAuthTests(AppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/manifest")]
    [InlineData("/img/anything/display")]
    [InlineData("/img/anything/thumb")]
    public async Task Api_and_image_paths_return_401_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/show")]
    [InlineData("/admin/queue")]
    [InlineData("/admin/images")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/telegram")]
    [InlineData("/upload")]
    public async Task Pages_redirect_to_login_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/login")]
    public async Task Open_paths_are_reachable_without_a_session(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_does_not_set_a_session_cookie()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.PostAsync("/login",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("password", "wrong")]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // No Set-Cookie header at all is the expected (and correct) outcome here, so
        // GetValues (which throws when the header is absent) would be the wrong call.
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : [];
        Assert.DoesNotContain(cookies, v => v.Contains($"{SessionCookie.Name}="));
    }

    [Fact]
    public async Task The_right_password_sets_a_session_cookie()
    {
        var client = _factory.CreateAnonymousClient();
        var before = DateTimeOffset.UtcNow;
        var response = await client.PostAsync("/login",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("password", AppFactory.Password)]));

        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.Contains($"{SessionCookie.Name}="));
        Assert.Contains(cookies, c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));

        // Expiry must be consistent with SessionCookie.Lifetime (14 days), not some
        // other value baked into the login handler by mistake.
        var sessionCookie = cookies.Single(c => c.Contains($"{SessionCookie.Name}="));
        var expiresMatch = System.Text.RegularExpressions.Regex.Match(
            sessionCookie, @"expires=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(expiresMatch.Success, $"No expires attribute on cookie: {sessionCookie}");
        var expires = DateTimeOffset.Parse(expiresMatch.Groups[1].Value);
        var expectedExpiry = before.Add(SessionCookie.Lifetime);
        Assert.True(
            Math.Abs((expires - expectedExpiry).TotalMinutes) < 5,
            $"Expected expiry near {expectedExpiry}, got {expires}");
    }
}
