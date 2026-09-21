using System.Net;

namespace EventPhotoBot.Tests;

/// <summary>
/// The upload page is the one surface whose value lives entirely in its markup:
/// everything it does goes through POST /api/images, which is tested elsewhere.
/// What these assert is the picker contract with the phone — get an attribute
/// wrong and the page still loads, still uploads, and silently does the one
/// thing it exists not to do.
/// </summary>
public class UploadPageTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public UploadPageTests(AppFactory factory) => _factory = factory;

    private async Task<string> PageAsync()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/upload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_page_is_served_to_a_session_as_html()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/upload");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_file_input_takes_several_photos_at_once()
    {
        Assert.Matches("""<input[^>]*\stype="file"[^>]*\smultiple""", await PageAsync());
    }

    [Fact]
    public async Task The_file_input_asks_for_the_photo_library_rather_than_the_camera()
    {
        // `capture` forces the camera and, on both iOS and Android, collapses the
        // picker to a single shot — multi-select is gone with no visible error.
        var page = await PageAsync();

        Assert.Contains("""accept="image/*" """.TrimEnd(), page, StringComparison.Ordinal);
        Assert.DoesNotMatch("""<input[^>]*\scapture[\s=>]""", page);
    }
}
