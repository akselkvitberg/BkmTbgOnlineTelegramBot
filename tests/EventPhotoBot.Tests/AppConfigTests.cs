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
        ("ADMIN_PASSWORD", "hunter2"),
        ("JOIN_CODE", "party2026"),
    ];

    [Fact]
    public void Load_binds_every_value()
    {
        var config = AppConfig.Load(Config(Complete()));

        Assert.Equal("bucket", config.BucketName);
        Assert.Equal("token", config.BotToken);
        Assert.Equal("hunter2", config.AdminPassword);
    }

    [Fact]
    public void Only_the_bot_token_and_admin_password_are_required()
    {
        // The four values that used to be secrets of their own are derived; a
        // deployment that no longer sets them must still start.
        var config = AppConfig.Load(Config(("BUCKET_NAME", "bucket"),
            ("TELEGRAM_BOT_TOKEN", "token"), ("ADMIN_PASSWORD", "hunter2")));

        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.WebhookSecretLabel), config.WebhookSecret);
        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.WebhookPathLabel), config.WebhookPath);
        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.CookieSigningKeyLabel), config.CookieSigningKey);
        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.RetentionSecretLabel), config.RetentionSecret);
    }

    [Fact]
    public void The_old_secret_variables_are_ignored_if_still_set()
    {
        // A revision deployed before the old secrets were removed from Terraform would
        // still carry them; the derived values must win, or the webhook the deploy
        // script registered stops matching.
        var pairs = Complete().Concat([
            ("TELEGRAM_WEBHOOK_SECRET", "old"), ("TELEGRAM_WEBHOOK_PATH", "old"),
            ("COOKIE_SIGNING_KEY", "old"), ("RETENTION_SECRET", "old")]).ToArray();

        var config = AppConfig.Load(Config(pairs));

        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.WebhookPathLabel), config.WebhookPath);
        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.RetentionSecretLabel), config.RetentionSecret);
    }

    [Fact]
    public void Load_throws_and_names_every_missing_key()
    {
        var partial = Complete().Where(p =>
            p.Item1 is not ("ADMIN_PASSWORD" or "TELEGRAM_BOT_TOKEN")).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(partial)));

        Assert.Contains("ADMIN_PASSWORD", error.Message);
        Assert.Contains("TELEGRAM_BOT_TOKEN", error.Message);
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
            .Select(p => p.Item1 == "ADMIN_PASSWORD" ? (p.Item1, "   ") : p).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(Config(blanked)));

        Assert.Contains("ADMIN_PASSWORD", error.Message);
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
        Assert.Equal("hunter2", config.AdminPassword);
        // Derived from the trimmed token, so they match what the deploy scripts register.
        Assert.Equal(DerivedSecrets.Derive("token", DerivedSecrets.WebhookSecretLabel), config.WebhookSecret);
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
    public void Load_accepts_a_missing_join_code()
    {
        var partial = Complete().Where(p => p.Item1 is not "JOIN_CODE").ToArray();

        Assert.Null(AppConfig.Load(Config(partial)).JoinCode);
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
