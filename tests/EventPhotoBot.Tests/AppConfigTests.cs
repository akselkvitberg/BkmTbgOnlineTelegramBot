using EventPhotoBot;
using Microsoft.Extensions.Configuration;

namespace EventPhotoBot.Tests;

public class AppConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static (string, string)[] Complete() =>
    [
        ("BUCKET_NAME", "bucket"),
        ("TELEGRAM_BOT_TOKEN", "token"),
        ("TELEGRAM_WEBHOOK_SECRET", "secret"),
        ("TELEGRAM_WEBHOOK_PATH", "abc123"),
        ("ADMIN_PASSWORD", "hunter2"),
        ("COOKIE_SIGNING_KEY", "0123456789abcdef0123456789abcdef"),
        ("JOIN_CODE", "party2026"),
    ];

    [Fact]
    public void Load_binds_every_value()
    {
        var config = AppConfig.Load(Config(Complete()));

        Assert.Equal("bucket", config.BucketName);
        Assert.Equal("abc123", config.WebhookPath);
    }

    [Fact]
    public void Load_throws_and_names_every_missing_key()
    {
        var partial = Complete().Where(p =>
            p.Item1 is not ("ADMIN_PASSWORD" or "COOKIE_SIGNING_KEY")).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(partial)));

        Assert.Contains("ADMIN_PASSWORD", error.Message);
        Assert.Contains("COOKIE_SIGNING_KEY", error.Message);
    }

    [Fact]
    public void Load_treats_an_empty_string_as_missing()
    {
        var blanked = Complete()
            .Select(p => p.Item1 == "TELEGRAM_BOT_TOKEN" ? (p.Item1, "") : p).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(blanked)));

        Assert.Contains("TELEGRAM_BOT_TOKEN", error.Message);
    }

    [Fact]
    public void Load_treats_a_whitespace_only_value_as_missing()
    {
        var blanked = Complete()
            .Select(p => p.Item1 == "TELEGRAM_WEBHOOK_SECRET" ? (p.Item1, "   ") : p).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(blanked)));

        Assert.Contains("TELEGRAM_WEBHOOK_SECRET", error.Message);
    }

    [Fact]
    public void Load_trims_a_trailing_newline_from_every_value()
    {
        // `gcloud secrets versions add --data-file=-` run interactively stores whatever
        // the terminal sends on Enter, trailing newline included — this is exactly that.
        var withNewlines = Complete()
            .Select(p => (p.Item1, p.Item2 + "\n")).ToArray();

        var config = AppConfig.Load(Config(withNewlines));

        Assert.Equal("bucket", config.BucketName);
        Assert.Equal("token", config.BotToken);
        Assert.Equal("secret", config.WebhookSecret);
        Assert.Equal("abc123", config.WebhookPath);
        Assert.Equal("hunter2", config.AdminPassword);
        Assert.Equal("0123456789abcdef0123456789abcdef", config.CookieSigningKey);
    }

    [Fact]
    public void Load_trims_leading_and_trailing_whitespace_from_every_value()
    {
        var padded = Complete().Select(p => (p.Item1, $"  {p.Item2}  ")).ToArray();

        var config = AppConfig.Load(Config(padded));

        Assert.Equal("hunter2", config.AdminPassword);
    }

    [Fact]
    public void Load_binds_the_join_code()
    {
        var config = AppConfig.Load(Config(Complete()));

        Assert.Equal("party2026", config.JoinCode);
    }

    [Fact]
    public void Load_throws_when_the_join_code_is_missing()
    {
        var partial = Complete().Where(p => p.Item1 is not "JOIN_CODE").ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(partial)));

        Assert.Contains("JOIN_CODE", error.Message);
    }

    [Theory]
    [InlineData("party 2026")]      // space
    [InlineData("party!")]          // punctuation outside the payload charset
    [InlineData("rødt")]            // non-ASCII
    public void Load_rejects_a_join_code_outside_the_deep_link_charset(string code)
    {
        var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
            .Append(("JOIN_CODE", code)).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(pairs)));

        Assert.Contains("JOIN_CODE", error.Message);
    }

    [Fact]
    public void Load_rejects_a_join_code_over_sixty_four_characters()
    {
        var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
            .Append(("JOIN_CODE", new string('a', 65))).ToArray();

        Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(pairs)));
    }

    [Fact]
    public void A_join_code_with_a_trailing_newline_is_trimmed_and_accepted()
    {
        var pairs = Complete().Where(p => p.Item1 is not "JOIN_CODE")
            .Append(("JOIN_CODE", "party2026\n")).ToArray();

        Assert.Equal("party2026", AppConfig.Load(Config(pairs)).JoinCode);
    }
}
