using System.Text;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Eds.Core.App;

/// <summary>One directory's worth of extracted metadata (name + key/value tags).</summary>
public sealed record ImageMetadataGroup(string Name, IReadOnlyList<(string Tag, string Value)> Tags);

/// <summary>
/// Extracts image metadata (EXIF, IPTC, dimensions, GPS, …) from a <b>decrypted</b>
/// stream inside a mounted location, so the image viewer can show it without ever
/// writing the plaintext to disk. UI-agnostic: shared by the MAUI and Avalonia
/// heads. Backed by <c>MetadataExtractor</c> (the .NET port of the same library the
/// original Android app used).
/// </summary>
public static class ImageMetadataReader
{
    /// <summary>File extensions the metadata reader / viewer will attempt to open.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".heic", ".heif", ".ico", ".psd",
    };

    public static bool IsImage(IPath path)
    {
        // IPath exposes PathString (GetPathUtil() lives on the PathBase impl, not the interface).
        string name = new StringPathUtil(path.PathString).GetFileName();
        int dot = name.LastIndexOf('.');
        return dot >= 0 && SupportedExtensions.Contains(name[dot..]);
    }

    /// <summary>
    /// Reads the whole decrypted file into memory and extracts its metadata groups.
    /// Reading fully (rather than streaming) avoids seek quirks across the encrypted
    /// stream layers and keeps images bounded to viewer-sized files.
    /// </summary>
    public static IReadOnlyList<ImageMetadataGroup> Read(IFile file)
    {
        using var input = file.GetInputStream();
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        return Read(ms);
    }

    public static IReadOnlyList<ImageMetadataGroup> Read(Stream seekableStream)
    {
        var groups = new List<ImageMetadataGroup>();
        IEnumerable<MetadataExtractor.Directory> directories;
        try
        {
            directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(seekableStream);
        }
        catch (ImageProcessingException)
        {
            return groups; // unrecognised / not an image
        }

        foreach (var dir in directories)
        {
            var tags = new List<(string, string)>();
            foreach (var tag in dir.Tags)
                tags.Add((tag.Name, tag.Description ?? string.Empty));
            if (dir.HasError)
                foreach (var err in dir.Errors)
                    tags.Add(("(error)", err));
            groups.Add(new ImageMetadataGroup(dir.Name, tags));
        }
        return groups;
    }

    /// <summary>Flattens the metadata to a readable multi-line string for a details pane.</summary>
    public static string Format(IReadOnlyList<ImageMetadataGroup> groups)
    {
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            sb.Append("── ").Append(g.Name).Append(" ──").AppendLine();
            foreach (var (tag, value) in g.Tags)
                sb.Append(tag).Append(": ").Append(value).AppendLine();
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Best-effort GPS coordinate extraction (decimal degrees), if the image carries
    /// a GPS EXIF directory. Returns null when absent.
    /// </summary>
    public static (double Latitude, double Longitude)? TryGetGps(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var loc = gps?.GetGeoLocation(); // already decimal degrees; null when absent
        return loc != null ? (loc.Latitude, loc.Longitude) : null;
    }
}
