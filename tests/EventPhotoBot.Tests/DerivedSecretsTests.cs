using EventPhotoBot;

namespace EventPhotoBot.Tests;

public class DerivedSecretsTests
{
    private const string Token = "123456:ABC-test_token";

    /// <summary>
    /// Pinned to what the deploy scripts compute, from
    /// <c>printf '%s' LABEL | openssl dgst -sha256 -hmac TOKEN -hex</c>. If the app and
    /// the scripts ever disagree, the webhook Telegram calls stops matching the
    /// route the app serves, and every update is lost.
    /// </summary>
    [Theory]
    [InlineData(DerivedSecrets.WebhookSecretLabel, "474f8b9cc98c900ff2a6a38275122f2e4cfccc0f32089ee560e137cc3437c6fd")]
    [InlineData(DerivedSecrets.WebhookPathLabel, "86cb55c3e77fab046fb1f7c0661c186a14463d114c89c77e02b9b02f58a376e7")]
    [InlineData(DerivedSecrets.CookieSigningKeyLabel, "8a731f2d5fbaa5bed63893f19078ad973f556d64d3af5a8bd9f0eb8dba1afb02")]
    [InlineData(DerivedSecrets.RetentionSecretLabel, "61e52ae4a8e6e921f5854e053fae9c076c446f059024773d183d6f331f9de1c1")]
    public void Matches_what_the_deploy_scripts_compute_with_openssl(string label, string expected)
    {
        Assert.Equal(expected, DerivedSecrets.Derive(Token, label));
    }

    [Fact]
    public void Each_label_gives_a_different_value_and_so_does_each_token()
    {
        string[] labels =
        [
            DerivedSecrets.WebhookSecretLabel, DerivedSecrets.WebhookPathLabel,
            DerivedSecrets.CookieSigningKeyLabel, DerivedSecrets.RetentionSecretLabel,
        ];

        Assert.Equal(4, labels.Select(l => DerivedSecrets.Derive(Token, l)).Distinct().Count());
        Assert.NotEqual(DerivedSecrets.Derive(Token, labels[0]), DerivedSecrets.Derive("other", labels[0]));
    }

    [Fact]
    public void The_webhook_secret_fits_telegrams_secret_token_rules()
    {
        // setWebhook's secret_token: 1-256 characters, A-Z a-z 0-9 _ - only.
        var secret = DerivedSecrets.Derive(Token, DerivedSecrets.WebhookSecretLabel);

        Assert.Matches("^[A-Za-z0-9_-]{1,256}$", secret);
    }
}
