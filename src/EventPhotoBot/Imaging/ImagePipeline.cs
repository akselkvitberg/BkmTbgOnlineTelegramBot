using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EventPhotoBot.Imaging;

public sealed record ProcessedImage(
    byte[] Display, byte[] Thumb, int Width, int Height, string Sha256);

/// <summary>
/// Thrown for an image that decodes to something the service should decline rather
/// than attempt to process. The message is written to be shown to the sender as-is.
/// </summary>
public sealed class ImageTooLargeException(string message) : Exception(message);

public static class ImagePipeline
{
    public const int DisplayMaxEdge = 2560;
    public const int ThumbMaxEdge = 480;
    public const int JpegQuality = 82;

    // The 20 MB Telegram download cap bounds compressed bytes, not the decoded
    // bitmap: a high-megapixel JPEG well under 20 MB can decode to hundreds of MB of
    // raw pixels. On the single 1 GiB Cloud Run instance this runs on
    // (max_instance_count = 1), that is an OOM that takes the slideshow down, not
    // just a failed upload. 50 megapixels covers any real camera photo with room to
    // spare while keeping the decoded bitmap in the tens-of-MB range.
    public const long MaxDecodedPixels = 50_000_000;

    private static readonly HashSet<string> Supported =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg", "image/png", "image/webp" };

    // The HEIF brands an Apple device actually writes. ImageSharp decodes none of
    // them, so the only use for this list is telling the two failures apart: a
    // photo in a format we cannot read, versus a file that is not a photo at all.
    private static readonly HashSet<string> HeifBrands =
        new(StringComparer.Ordinal)
        {
            "heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1",
        };

    public static bool IsSupportedMimeType(string? mimeType) =>
        mimeType is not null && Supported.Contains(mimeType);

    /// <summary>
    /// Sniffs the ISO base media file format header an iPhone photo carries. Every
    /// such file opens with a four-byte box length, the literal "ftyp", and a brand;
    /// that is all this reads, and it never touches pixel data.
    /// </summary>
    public static bool LooksLikeHeif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12
        && bytes[4..8].SequenceEqual("ftyp"u8)
        && HeifBrands.Contains(Encoding.ASCII.GetString(bytes[8..12]));

    /// <summary>
    /// Decode, apply EXIF orientation, strip metadata, produce both derivatives.
    /// Orientation is applied before resizing: a portrait photo landing sideways
    /// on the projector is the single most common visible bug in this system.
    /// </summary>
    public static ProcessedImage Process(byte[] original)
    {
        // Identify reads only the header, not the pixel data, so this stays cheap
        // even for the file this check exists to reject.
        var info = Image.Identify(original)
                   ?? throw new InvalidOperationException("Unrecognized image format.");
        var pixels = (long)info.Width * info.Height;
        if (pixels > MaxDecodedPixels)
            throw new ImageTooLargeException(
                "Det bildet er for stort til at jeg får behandlet det — prøv å sende et mindre.");

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
