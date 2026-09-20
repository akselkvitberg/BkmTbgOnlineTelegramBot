using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EventPhotoBot.Imaging;

public sealed record ProcessedImage(
    byte[] Display, byte[] Thumb, int Width, int Height, string Sha256);

public static class ImagePipeline
{
    public const int DisplayMaxEdge = 2560;
    public const int ThumbMaxEdge = 480;
    public const int JpegQuality = 82;

    private static readonly HashSet<string> Supported =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg", "image/png", "image/webp" };

    public static bool IsSupportedMimeType(string? mimeType) =>
        mimeType is not null && Supported.Contains(mimeType);

    /// <summary>
    /// Decode, apply EXIF orientation, strip metadata, produce both derivatives.
    /// Orientation is applied before resizing: a portrait photo landing sideways
    /// on the projector is the single most common visible bug in this system.
    /// </summary>
    public static ProcessedImage Process(byte[] original)
    {
        using var image = Image.Load(original);

        image.Mutate(c => c.AutoOrient());

        // Strip everything that could carry GPS coordinates or device identifiers.
        // The display copy is the one that gets served around.
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IccProfile = null;

        var display = Encode(image, DisplayMaxEdge);
        var thumb = Encode(image, ThumbMaxEdge);

        var (width, height) = Scaled(image.Width, image.Height, DisplayMaxEdge);

        return new ProcessedImage(
            display, thumb, width, height, Convert.ToHexStringLower(SHA256.HashData(display)));
    }

    private static byte[] Encode(Image source, int maxEdge)
    {
        var (width, height) = Scaled(source.Width, source.Height, maxEdge);

        using var resized = source.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3,
        }));

        using var buffer = new MemoryStream();
        resized.Save(buffer, new JpegEncoder { Quality = JpegQuality });
        return buffer.ToArray();
    }

    /// <summary>Scales down to fit the long edge. Never scales up.</summary>
    private static (int Width, int Height) Scaled(int width, int height, int maxEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxEdge) return (width, height);

        var factor = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * factor)),
                Math.Max(1, (int)Math.Round(height * factor)));
    }
}
