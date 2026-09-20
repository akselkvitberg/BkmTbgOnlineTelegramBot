using EventPhotoBot.Web;

namespace EventPhotoBot.Tests;

public class SessionCookieTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_freshly_issued_cookie_is_valid()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddDays(14));
        Assert.True(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void An_expired_cookie_is_rejected()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddMinutes(-1));
        Assert.False(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void A_cookie_signed_with_a_different_key_is_rejected()
    {
        var cookie = SessionCookie.Issue("ffffffffffffffffffffffffffffffff", Now.AddDays(1));
        Assert.False(SessionCookie.IsValid(Key, cookie, Now));
    }

    [Fact]
    public void A_tampered_expiry_is_rejected()
    {
        var cookie = SessionCookie.Issue(Key, Now.AddMinutes(-1));
        var forged = $"{Now.AddDays(1).ToUnixTimeSeconds()}.{cookie.Split('.')[1]}";
        Assert.False(SessionCookie.IsValid(Key, forged, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("123")]
    [InlineData("notanumber.abcd")]
    public void Malformed_cookies_are_rejected_without_throwing(string? value)
    {
        Assert.False(SessionCookie.IsValid(Key, value, Now));
    }

    [Fact]
    public void Password_comparison_accepts_the_right_password()
    {
        Assert.True(SessionCookie.PasswordMatches("hunter2", "hunter2"));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("hunter")]
    [InlineData("hunter22")]
    [InlineData("")]
    [InlineData(null)]
    public void Password_comparison_rejects_everything_else(string? supplied)
    {
        Assert.False(SessionCookie.PasswordMatches("hunter2", supplied));
    }
}
