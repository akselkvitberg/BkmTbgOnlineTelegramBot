using System.Net;
using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ManifestEndpointTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public ManifestEndpointTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_returns_an_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task A_matching_if_none_match_returns_304_with_no_body()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await client.GetAsync("/api/manifest");
        var etag = first.Headers.ETag!.ToString();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_poll_performs_no_object_store_io()
    {
        var client = _factory.CreateAuthenticatedClient();
        var first = await client.GetAsync("/api/manifest");
        var etag = first.Headers.ETag!.ToString();

        var readsBefore = _factory.Objects.ReadCount;
        var writesBefore = _factory.Objects.WriteCount;

        for (var i = 0; i < 20; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/manifest");
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            await client.SendAsync(request);
        }

        Assert.Equal(readsBefore, _factory.Objects.ReadCount);
        Assert.Equal(writesBefore, _factory.Objects.WriteCount);
    }

    [Fact]
    public async Task Changing_state_changes_the_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var before = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();

        await _factory.Store.MutateAsync(s => s.Settings.SlideSeconds = 11);

        var after = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Image_bytes_are_served_from_the_object_store()
    {
        await _factory.Objects.WriteAsync(
            ObjectPaths.Display("img1"), [1, 2, 3, 4], "image/jpeg", null);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/img1/display");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_image_returns_404()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/nope/display");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Image_responses_carry_an_immutable_cache_header()
    {
        await _factory.Objects.WriteAsync(
            ObjectPaths.Display("img2"), [9], "image/jpeg", null);

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/img2/display");

        var cacheControl = response.Headers.CacheControl!;
        Assert.True(cacheControl.Private);
        Assert.True(cacheControl.MaxAge > TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task An_unauthenticated_image_401_carries_no_immutable_cache_header()
    {
        // A 401 must never be cacheable: a viewer who hits an image before logging
        // in must not be stuck with a year-long cached rejection after logging in.
        await _factory.Objects.WriteAsync(
            ObjectPaths.Display("img3"), [7], "image/jpeg", null);

        var response = await _factory.CreateAnonymousClient().GetAsync("/img/img3/display");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.CacheControl?.MaxAge);
        Assert.False(response.Headers.CacheControl?.Private ?? false);
    }

    [Fact]
    public async Task A_missing_image_404_carries_no_immutable_cache_header()
    {
        // Same story for a 404: the id might exist a moment later, so a
        // year-long immutable cache on "not found" would hide it forever.
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/img/nope-again/display");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.CacheControl?.MaxAge);
        Assert.False(response.Headers.CacheControl?.Private ?? false);
    }
}
