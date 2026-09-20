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
        ("EVENT_NAME", "Party"),
        ("TELEGRAM_BOT_TOKEN", "token"),
        ("TELEGRAM_WEBHOOK_SECRET", "secret"),
        ("TELEGRAM_WEBHOOK_PATH", "abc123"),
        ("ADMIN_PASSWORD", "hunter2"),
        ("COOKIE_SIGNING_KEY", "0123456789abcdef0123456789abcdef"),
    ];

    [Fact]
    public void Load_binds_every_value()
    {
        var config = AppConfig.Load(Config(Complete()));

        Assert.Equal("bucket", config.BucketName);
        Assert.Equal("Party", config.EventName);
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
}
