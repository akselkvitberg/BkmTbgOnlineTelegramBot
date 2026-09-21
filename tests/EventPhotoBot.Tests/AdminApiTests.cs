using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EventPhotoBot.State;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace EventPhotoBot.Tests;

public class AdminApiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;

    public AdminApiTests(AppFactory factory) => _factory = factory;

    private async Task<string> SeedImageAsync(ImageStatus status = ImageStatus.Pending)
    {
        var id = Guid.NewGuid().ToString("N");
        await _factory.Objects.WriteAsync(ObjectPaths.Display(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Thumb(id), [1], "image/jpeg", null);
        await _factory.Objects.WriteAsync(ObjectPaths.Original(id, "jpg"), [1], "image/jpeg", null);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, Sha256 = id, SortKey = id, Status = status,
            Width = 10, Height = 10, OriginalExtension = "jpg",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Approving_an_image_sets_status_and_stamps_the_decision_time()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images[id];
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task An_unknown_status_value_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        var response = await client.PostAsJsonAsync($"/api/images/{id}/status", new { status = "banana" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setting_a_recurring_pin_works()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync(ImageStatus.Approved);

        await client.PostAsJsonAsync($"/api/images/{id}/pin", new { pin = "recurring" });

        Assert.Equal(PinKind.Recurring, _factory.Store.Snapshot.Images[id].Pin);
    }

    [Fact]
    public async Task Deleting_an_image_removes_the_entry_and_every_object()
    {
        var client = _factory.CreateAuthenticatedClient();
        var id = await SeedImageAsync();

        await client.DeleteAsync($"/api/images/{id}");

        Assert.DoesNotContain(id, _factory.Store.Snapshot.Images.Keys);
        Assert.DoesNotContain(ObjectPaths.Display(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Thumb(id), _factory.Objects.Paths);
        Assert.DoesNotContain(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Deleting_an_unknown_image_returns_404()
    {
        var response = await _factory.CreateAuthenticatedClient().DeleteAsync("/api/images/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_upload_arrives_approved_with_all_three_objects()
    {
        var client = _factory.CreateAuthenticatedClient();
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "programme.jpg");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var image = _factory.Store.Snapshot.Images.Values
            .Single(i => i.Source == ImageSource.Admin);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.Null(image.SenderId);
        Assert.Contains(ObjectPaths.Display(image.Id), _factory.Objects.Paths);
    }

    [Fact]
    public async Task An_upload_with_an_unrecognized_extension_falls_back_to_jpg_rather_than_using_it_verbatim()
    {
        // The extension becomes part of a GCS object name, so a client-supplied
        // IFormFile.FileName must never flow into it unvalidated.
        var client = _factory.CreateAuthenticatedClient();
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "../../etc/passwd.exe");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;
        var image = _factory.Store.Snapshot.Images[id];
        Assert.Equal("jpg", image.OriginalExtension);
        Assert.Contains(ObjectPaths.Original(id, "jpg"), _factory.Objects.Paths);
    }

    [Fact]
    public async Task Uploading_something_that_is_not_an_image_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "notes.txt");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The one rejection a phone will actually produce. An iPhone hands Safari a JPEG
    /// when the photo comes from the library picker, but a photo picked through the
    /// Files app can arrive as HEIC, which ImageSharp cannot decode. The generic
    /// "not an image" message leaves the uploader with no idea what to change, so
    /// this case is sniffed and answered by name.
    /// </summary>
    [Fact]
    public async Task A_heic_upload_is_rejected_by_name_rather_than_as_an_unreadable_file()
    {
        var client = _factory.CreateAuthenticatedClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(HeicHeader());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/heic");
        content.Add(file, "file", "IMG_0001.heic");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("HEIC", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_upload_over_the_decode_pixel_cap_keeps_the_pipeline_s_own_explanation()
    {
        // ImagePipeline throws ImageTooLargeException with a message written to be
        // shown to the sender; the endpoint used to swallow it into the generic
        // "not an image" reply, which is wrong — the file is a perfectly good image.
        var client = _factory.CreateAuthenticatedClient();

        using var oversized = new Image<L8>(8000, 6252); // 50,016,000 px > the cap
        using var buffer = new MemoryStream();
        oversized.Save(buffer, new PngEncoder());

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(buffer.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "huge.png");

        var response = await client.PostAsync("/api/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("for stort", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bare ISO-BMFF ftyp box with the heic brand — the first twelve bytes of any
    /// photo an iPhone stores in HEIF, and all the sniffing looks at.
    /// </summary>
    private static byte[] HeicHeader() =>
    [
        0x00, 0x00, 0x00, 0x18,
        .. "ftyp"u8.ToArray(),
        .. "heic"u8.ToArray(),
        0x00, 0x00, 0x00, 0x00,
        .. "mif1"u8.ToArray(),
        .. "heic"u8.ToArray(),
    ];

    [Fact]
    public async Task Settings_can_be_patched_one_field_at_a_time()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 12 });
        await client.PatchAsJsonAsync("/api/settings", new { order = "newest-first" });

        var settings = _factory.Store.Snapshot.Settings;
        Assert.Equal(12, settings.SlideSeconds);
        Assert.Equal(SlideOrder.NewestFirst, settings.Order);
        Assert.Equal(800, settings.TransitionMs); // untouched fields survive
    }

    [Fact]
    public async Task Ken_burns_can_be_turned_off_and_back_on()
    {
        var client = _factory.CreateAuthenticatedClient();
        Assert.True(_factory.Store.Snapshot.Settings.KenBurns); // on unless asked otherwise

        await client.PatchAsJsonAsync("/api/settings", new { kenBurns = false });
        Assert.False(_factory.Store.Snapshot.Settings.KenBurns);

        await client.PatchAsJsonAsync("/api/settings", new { kenBurns = true });
        Assert.True(_factory.Store.Snapshot.Settings.KenBurns);
    }

    [Fact]
    public async Task The_join_invite_can_be_turned_off_and_back_on()
    {
        var client = _factory.CreateAuthenticatedClient();
        Assert.True(_factory.Store.Snapshot.Settings.ShowJoinInvite); // on unless asked otherwise

        await client.PatchAsJsonAsync("/api/settings", new { showJoinInvite = false });
        Assert.False(_factory.Store.Snapshot.Settings.ShowJoinInvite);

        await client.PatchAsJsonAsync("/api/settings", new { showJoinInvite = true });
        Assert.True(_factory.Store.Snapshot.Settings.ShowJoinInvite);
    }

    // The store is shared across this class, so these set the layout they start from
    // rather than assuming the default - a sibling test that changed it would
    // otherwise decide whether this one passes. SlideLayout.Single as the default is
    // asserted where it belongs, against a fresh EventState in ManifestBuilderTests.

    [Fact]
    public async Task The_layout_can_be_changed_from_admin()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PatchAsJsonAsync("/api/settings", new { layout = "single" });

        await client.PatchAsJsonAsync("/api/settings", new { layout = "mosaic" });
        Assert.Equal(SlideLayout.Mosaic, _factory.Store.Snapshot.Settings.Layout);

        await client.PatchAsJsonAsync("/api/settings", new { layout = "split" });
        Assert.Equal(SlideLayout.Split, _factory.Store.Snapshot.Settings.Layout);
    }

    [Theory]
    [InlineData("banana")]
    // Enum.TryParse parses a numeric string too, and would hand back an undefined
    // enum value to be written to the state file and served to the screen as a
    // layout name nothing has ever heard of.
    [InlineData("99")]
    [InlineData("")]
    public async Task An_unknown_layout_is_rejected_and_changes_nothing(string layout)
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PatchAsJsonAsync("/api/settings", new { layout = "polaroid" });

        var response = await client.PatchAsJsonAsync("/api/settings", new { layout });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SlideLayout.Polaroid, _factory.Store.Snapshot.Settings.Layout);
    }

    [Fact]
    public async Task Patching_other_settings_leaves_the_layout_alone()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PatchAsJsonAsync("/api/settings", new { layout = "collage" });

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 6 });

        Assert.Equal(SlideLayout.Collage, _factory.Store.Snapshot.Settings.Layout);
    }

    [Fact]
    public async Task The_event_name_can_be_set_and_changed_from_admin()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new { eventName = "  Sommerfest 2026  " });

        // Trimmed: the field is typed into a web form, and a stray space would
        // show up centred on the projector.
        Assert.Equal("Sommerfest 2026", _factory.Store.Snapshot.Settings.EventName);
    }

    [Fact]
    public async Task An_over_long_event_name_is_truncated_rather_than_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.PatchAsJsonAsync("/api/settings", new { eventName = new string('a', 300) });

        Assert.Equal(100, _factory.Store.Snapshot.Settings.EventName.Length);
    }

    [Fact]
    public async Task The_event_name_can_be_cleared()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PatchAsJsonAsync("/api/settings", new { eventName = "Sommerfest" });

        await client.PatchAsJsonAsync("/api/settings", new { eventName = "" });

        Assert.Equal("", _factory.Store.Snapshot.Settings.EventName);
    }

    [Fact]
    public async Task A_settings_change_advances_the_manifest_etag()
    {
        var client = _factory.CreateAuthenticatedClient();
        var before = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();

        await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 7 });

        var after = (await client.GetAsync("/api/manifest")).Headers.ETag!.ToString();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Takeover_with_a_missing_imageId_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/api/takeover", new { minutes = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Takeover_with_a_null_imageId_is_rejected()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync(
            "/api/takeover", new { imageId = (string?)null, minutes = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Listing_images_filters_by_status_and_shapes_each_entry()
    {
        var client = _factory.CreateAuthenticatedClient();
        var pendingId = await SeedImageAsync(ImageStatus.Pending);

        var response = await client.GetAsync("/api/images?status=pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = document.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == pendingId);
        Assert.Equal("pending", entry.GetProperty("status").GetString());
        Assert.Equal("none", entry.GetProperty("pin").GetString());
        Assert.Equal(10, entry.GetProperty("width").GetInt32());
        Assert.Equal(10, entry.GetProperty("height").GetInt32());
        Assert.True(entry.TryGetProperty("receivedAt", out _));

        // A different filter must not surface this pending image.
        var approvedOnly = await client.GetAsync("/api/images?status=approved");
        using var approvedDoc = JsonDocument.Parse(await approvedOnly.Content.ReadAsStringAsync());
        Assert.DoesNotContain(approvedDoc.RootElement.EnumerateArray(),
            e => e.GetProperty("id").GetString() == pendingId);
    }

    [Fact]
    public async Task Listing_images_without_a_status_returns_images_of_every_status()
    {
        var client = _factory.CreateAuthenticatedClient();
        var pendingId = await SeedImageAsync(ImageStatus.Pending);
        var rejectedId = await SeedImageAsync(ImageStatus.Rejected);

        var response = await client.GetAsync("/api/images");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = document.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        Assert.Contains(pendingId, ids);
        Assert.Contains(rejectedId, ids);
    }

    [Fact]
    public async Task Listing_images_with_an_unknown_status_is_rejected()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/images?status=banana");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reading_settings_returns_the_full_shape_including_the_roster()
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.Store.MutateAsync(s =>
        {
            s.Settings.SlideSeconds = 42;
            s.Settings.Order = SlideOrder.NewestFirst;
            s.Settings.EventName = "Sommerfest";
            s.Settings.Layout = SlideLayout.Mosaic;
            s.Settings.Senders =
                [new Sender { Id = 42, Name = "Guest", Status = SenderStatus.AutoApprove }];
        });

        var response = await client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(42, root.GetProperty("slideSeconds").GetInt32());
        Assert.Equal("newest-first", root.GetProperty("order").GetString());
        Assert.True(root.TryGetProperty("transitionMs", out _));
        Assert.True(root.TryGetProperty("newestFirstBoost", out _));
        Assert.True(root.TryGetProperty("recurringEvery", out _));
        Assert.Equal("Sommerfest", root.GetProperty("eventName").GetString());
        Assert.True(root.TryGetProperty("kenBurns", out _));
        Assert.Equal("mosaic", root.GetProperty("layout").GetString());
        Assert.True(root.TryGetProperty("takeoverImageId", out _));
        Assert.True(root.TryGetProperty("takeoverUntil", out _));

        // The removed shape must not linger: an admin page still reading these would
        // silently render an empty table rather than fail.
        Assert.False(root.TryGetProperty("whitelist", out _));
        Assert.False(root.TryGetProperty("seenSenders", out _));
        Assert.False(root.TryGetProperty("pairingMode", out _));
        Assert.False(root.TryGetProperty("autoApproveTrusted", out _));

        var sender = Assert.Single(root.GetProperty("senders").EnumerateArray());
        Assert.Equal(42, sender.GetProperty("id").GetInt64());
        Assert.Equal("Guest", sender.GetProperty("name").GetString());
        Assert.Equal("autoApprove", sender.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_two_new_read_routes_require_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var responses = new[]
        {
            await client.GetAsync("/api/images"),
            await client.GetAsync("/api/settings"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task Every_mutating_route_requires_a_session()
    {
        var client = _factory.CreateAnonymousClient();

        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/images/anything/status", new { status = "approved" }),
            await client.PostAsJsonAsync("/api/images/anything/pin", new { pin = "recurring" }),
            await client.PutAsJsonAsync("/api/takeover", new { imageId = "anything", minutes = (int?)null }),
            await client.DeleteAsync("/api/takeover"),
            await client.DeleteAsync("/api/images/anything"),
            await client.PostAsync("/api/images", new MultipartFormDataContent()),
            await client.PatchAsJsonAsync("/api/settings", new { slideSeconds = 10 }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
