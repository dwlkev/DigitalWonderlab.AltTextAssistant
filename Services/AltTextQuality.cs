using System.Text.RegularExpressions;

namespace DigitalWonderlab.AltTextAssistant.Services;

/// <summary>
/// Plain rules (no AI) that spot alt text which exists but does not describe the image:
/// filenames, camera names, placeholder words, "image of..." and so on.
/// </summary>
public static class AltTextQuality
{
    // Alt text over this length is usually a caption or a paragraph, not alt text.
    public const int MaxLength = 150;

    // The same alt on this many different images is almost certainly copy-paste.
    public const int DuplicateThreshold = 3;

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "img", "photo", "photograph", "picture", "pic", "graphic", "logo", "banner", "icon",
        "untitled", "placeholder", "alt", "alt text", "alttext", "test", "thumbnail", "thumb", "hero",
        "header", "background", "bg", "screenshot", "null", "undefined", "none", "todo", "tbc", "tbd",
        "default", "spacer", "blank", "image 1", "photo 1", "new image", "media",
    };

    private static readonly Regex FileExtension =
        new(@"\.(jpe?g|png|gif|webp|svg|tiff?|bmp|heic|avif)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // IMG_1234, DSC01234, PXL_20240101_123456, Screenshot 2024-01-01, WhatsApp Image 2024...
    private static readonly Regex CameraName =
        new(@"^(img|dsc|dscn|dscf|dcim|pxl|mvimg|gopr|screenshot|screen shot|whatsapp image|image|photo)[\s_-]*\d{2,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A single "word" with a long digit run or a hash, e.g. 8f3a2c9d1e or banner_20240115.
    private static readonly Regex Machine =
        new(@"^[^\s]*(\d{4,}|[0-9a-f]{10,})[^\s]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Redundant =
        new(@"^(an? )?(image|picture|photo|photograph|graphic) (of|showing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lower case, no extension, separators to spaces: "Team_Photo-2.JPG" becomes "team photo 2".</summary>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var v = FileExtension.Replace(value.Trim(), "");
        v = Regex.Replace(v, @"[\s_\-\.]+", " ");
        return v.Trim().ToLowerInvariant();
    }

    /// <summary>Why this alt text needs improving; empty when it looks fine. Blank alt is not judged here.</summary>
    public static List<string> Problems(string? altText, string? mediaName, string? fileName, int usedOnImages = 1)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(altText)) return problems;

        var alt = altText.Trim();
        var norm = Normalise(alt);
        var file = Normalise(fileName);

        if (file.Length > 0 && norm == file)
            problems.Add("Same as the file name");
        else if (Normalise(mediaName) is { Length: > 0 } name && norm == name)
            problems.Add("Same as the media name");

        if (FileExtension.IsMatch(alt))
            problems.Add("Contains a file extension");
        if (CameraName.IsMatch(alt) || (!alt.Contains(' ') && Machine.IsMatch(alt)))
            problems.Add("Looks like a camera or system file name");
        if (Placeholders.Contains(norm))
            problems.Add("Placeholder word, not a description");
        else if (norm.Replace(" ", "").Length < 3)
            problems.Add("Too short to describe anything");
        if (Redundant.IsMatch(alt))
            problems.Add("Starts with \"image of\" (screen readers already say it is an image)");
        if (alt.Length > MaxLength)
            problems.Add($"Longer than {MaxLength} characters");
        if (usedOnImages >= DuplicateThreshold)
            problems.Add($"Same alt text used on {usedOnImages} images");

        return problems;
    }
}
