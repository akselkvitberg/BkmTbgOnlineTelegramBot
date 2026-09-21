using EventPhotoBot.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace EventPhotoBot.Tests;

public class ImagePipelineTests
{
    private static byte[] Asset(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", name));

    [Fact]
    public void Exif_orientation_is_applied_so_portrait_photos_come_out_upright()
    {
        var result = ImagePipeline.Process(Asset("portrait-exif-6.jpg"));

        using var display = Image.Load(result.Display);
        Assert.True(display.Height > display.Width,
            "An orientation-6 source is stored landscape and must come out portrait.");
        Assert.Equal(display.Width, result.Width);
        Assert.Equal(display.Height, result.Height);
    }

    [Fact]
    public void Derivatives_carry_no_exif_metadata()
    {
        var result = ImagePipeline.Process(Asset("portrait-exif-6.jpg"));

        using var display = Image.Load(result.Display);
        using var thumb = Image.Load(result.Thumb);
        Assert.Null(display.Metadata.ExifProfile);
        Assert.Null(thumb.Metadata.ExifProfile);
    }

    [Fact]
    public void The_display_derivative_is_capped_at_the_long_edge()
    {
        var result = ImagePipeline.Process(Asset("landscape.jpg"));

        using var display = Image.Load(result.Display);
        Assert.Equal(ImagePipeline.DisplayMaxEdge, Math.Max(display.Width, display.Height));
    }

    [Fact]
    public void The_thumbnail_is_capped_at_the_thumb_edge()
    {
        var result = ImagePipeline.Process(Asset("landscape.jpg"));

        using var thumb = Image.Load(result.Thumb);
        Assert.Equal(ImagePipeline.ThumbMaxEdge, Math.Max(thumb.Width, thumb.Height));
    }

    [Fact]
    public void Images_smaller_than_the_cap_are_not_upscaled()
    {
        var result = ImagePipeline.Process(Asset("tiny.png"));

        using var display = Image.Load(result.Display);
        Assert.Equal(64, display.Width);
        Assert.Equal(64, display.Height);
    }

    [Fact]
    public void Png_input_produces_jpeg_derivatives()
    {
        var result = ImagePipeline.Process(Asset("tiny.png"));

        var format = Image.DetectFormat(result.Display);
        Assert.Equal("JPEG", format.Name);
    }

    [Fact]
    public void The_hash_is_stable_for_identical_input_and_differs_for_different_input()
    {
        var one = ImagePipeline.Process(Asset("landscape.jpg"));
        var same = ImagePipeline.Process(Asset("landscape.jpg"));
        var other = ImagePipeline.Process(Asset("tiny.png"));

        Assert.Equal(one.Sha256, same.Sha256);
        Assert.NotEqual(one.Sha256, other.Sha256);
        Assert.Equal(64, one.Sha256.Length);
    }

    [Fact]
    public void Corrupt_input_throws_a_clear_exception_rather_than_producing_garbage()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.ThrowsAny<Exception>(() => ImagePipeline.Process(garbage));
    }

    /// <summary>
    /// The 20 MB Telegram download cap bounds compressed bytes, not the decoded
    /// bitmap. A real high-megapixel photo compresses down under that cap easily,
    /// but decodes to hundreds of MB of raw pixels — an OOM risk on the single 1 GiB
    /// Cloud Run instance this runs on. This builds a genuine (if visually blank)
    /// image just over the pixel cap, single-channel to keep the test itself cheap,
    /// and asserts it is declined by header inspection alone, before the expensive
    /// full decode (Image.Load) ever runs.
    /// </summary>
    [Fact]
    public void An_image_over_the_decode_pixel_cap_is_declined_politely()
    {
        const int width = 8000;
        const int height = 6252; // 8000 * 6252 = 50,016,000 > MaxDecodedPixels
        Assert.True((long)width * height > ImagePipeline.MaxDecodedPixels);

        using var oversized = new Image<L8>(width, height);
        using var buffer = new MemoryStream();
        oversized.Save(buffer, new PngEncoder());

        var error = Assert.Throws<ImageTooLargeException>(() => ImagePipeline.Process(buffer.ToArray()));
        Assert.Contains("for stort", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_at_the_decode_pixel_cap_is_still_processed()
    {
        // landscape.jpg (used throughout this file) is nowhere near the cap; this
        // just confirms Process() doesn't reject an ordinary photo the cap should
        // never touch.
        var result = ImagePipeline.Process(Asset("landscape.jpg"));
        Assert.True(result.Width > 0 && result.Height > 0);
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", false)]
    [InlineData("image/gif", false)]
    [InlineData("video/mp4", false)]
    [InlineData(null, false)]
    public void Supported_mime_types_exclude_heic_and_everything_non_photographic(
        string? mimeType, bool supported)
    {
        Assert.Equal(supported, ImagePipeline.IsSupportedMimeType(mimeType));
    }
}
