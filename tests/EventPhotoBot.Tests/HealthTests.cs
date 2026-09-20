namespace EventPhotoBot.Tests;

public class HealthTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public HealthTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_ok_without_a_session()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
