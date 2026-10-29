using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DigitalWonderlab.AltTextAssistant.Services;

/// <summary>
/// Turns a media file into bytes + MIME type that every vision provider accepts.
/// The MIME type comes from the file's magic bytes, never its extension: a .webp that is really a
/// JPEG (or the reverse) is rejected outright by Anthropic ("image appears to be image/jpeg").
/// Anything oversized, too large in pixels, or in a format the providers don't take is re-encoded.
/// </summary>
public static class AiImagePreparer
{
    // Anthropic caps images at 5MB of base64 (~3.75MB raw) and 8000px a side, and downsizes anything
    // over ~1568px anyway, so sending bigger only costs upload time.
    private const int MaxRawBytes = 3_500_000;
    private const int MaxEdge = 1568;

    public sealed record PreparedImage(byte[] Bytes, string MimeType, bool Reencoded);

    /// <summary>Thrown when the file is not an image the providers or ImageSharp can read (e.g. SVG).</summary>
    public sealed class UnsupportedImageException(string message) : Exception(message);

    public static PreparedImage Prepare(byte[] bytes)
    {
        var sniffed = SniffMimeType(bytes);

        if (sniffed != null && bytes.Length <= MaxRawBytes)
        {
            try
            {
                var info = Image.Identify(bytes);
                if (Math.Max(info.Width, info.Height) <= MaxEdge)
                    return new PreparedImage(bytes, sniffed, false);
            }
            catch
            {
                // Header unreadable by ImageSharp: send as-is and let the provider decide.
                return new PreparedImage(bytes, sniffed, false);
            }
        }

        return Reencode(bytes);
    }

    private static PreparedImage Reencode(byte[] bytes)
    {
        Image image;
        try
        {
            image = Image.Load(bytes);
        }
        catch (Exception ex)
        {
            throw new UnsupportedImageException(
                $"This file type cannot be read for AI suggestions ({ex.GetType().Name}). Add the alt text manually.");
        }

        using (image)
        {
            if (Math.Max(image.Width, image.Height) > MaxEdge)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxEdge, MaxEdge)
                }));
            }

            using var ms = new MemoryStream();
            // Keep transparency as PNG; everything else as JPEG (much smaller for photos).
            if (image.PixelType.AlphaRepresentation is { } alpha && alpha != PixelAlphaRepresentation.None)
            {
                image.Save(ms, new PngEncoder());
                return new PreparedImage(ms.ToArray(), "image/png", true);
            }

            image.Save(ms, new JpegEncoder { Quality = 85 });
            return new PreparedImage(ms.ToArray(), "image/jpeg", true);
        }
    }

    /// <summary>JPEG, PNG, GIF and WebP are the formats all three providers accept.</summary>
    internal static string? SniffMimeType(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "image/png";
        if (b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8')
            return "image/gif";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return "image/webp";
        return null;
    }
}
