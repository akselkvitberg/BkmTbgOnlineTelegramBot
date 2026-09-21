// Regenerates the three ImagePipelineTests fixtures (portrait-exif-6.jpg, landscape.jpg,
// tiny.png) into the TestAssets folder above this one. Run with:
//   dotnet run --project tests/EventPhotoBot.Tests/TestAssets/FixtureGenerator
// or pass an explicit output directory as the first argument. Depends only on
// SixLabors.ImageSharp (see FixtureGenerator.csproj for why SixLabors.ImageSharp.Drawing
// is deliberately not used here) — fills are done with a plain pixel loop.
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var outDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
Directory.CreateDirectory(outDir);

// A 400x800 portrait image stored as 800x400 landscape with orientation 6,
// which is how a phone records a portrait photo.
using (var image = new Image<Rgb24>(800, 400))
{
    FillRect(image, Color.CornflowerBlue.ToPixel<Rgb24>(), 0, 0, 800, 400);
    // Paint a stripe down what should be the LEFT edge after orientation is applied.
    FillRect(image, Color.Red.ToPixel<Rgb24>(), 0, 0, 800, 40);
    image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
    image.Metadata.ExifProfile.SetValue(
        SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
    image.Metadata.ExifProfile.SetValue(
        SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.GPSLatitudeRef, "N");
    image.SaveAsJpeg(Path.Combine(outDir, "portrait-exif-6.jpg"), new JpegEncoder { Quality = 90 });
}

using (var image = new Image<Rgb24>(4000, 3000))
{
    FillRect(image, Color.SeaGreen.ToPixel<Rgb24>(), 0, 0, 4000, 3000);
    image.SaveAsJpeg(Path.Combine(outDir, "landscape.jpg"), new JpegEncoder { Quality = 90 });
}

using (var image = new Image<Rgba32>(64, 64))
{
    FillRect(image, Color.Goldenrod.ToPixel<Rgba32>(), 0, 0, 64, 64);
    image.SaveAsPng(Path.Combine(outDir, "tiny.png"));
}

Console.WriteLine("Fixtures written to " + Path.GetFullPath(outDir));

static void FillRect<TPixel>(Image<TPixel> image, TPixel color, int x0, int y0, int w, int h)
    where TPixel : unmanaged, IPixel<TPixel>
{
    for (var y = y0; y < y0 + h && y < image.Height; y++)
        for (var x = x0; x < x0 + w && x < image.Width; x++)
            image[x, y] = color;
}
