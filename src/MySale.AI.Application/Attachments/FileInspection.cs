using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MySale.AI.Domain;

namespace MySale.AI.Application.Attachments;

public sealed record FileTypeInfo(string Kind, string Extension, string ContentType);

/// <summary>
/// Decides what an upload really is. The extension must be on the allow-list AND the content (magic bytes)
/// must match it; the browser-supplied MIME type is only used as a consistency hint. Executables never pass.
/// </summary>
public static class FileTypeDetector
{
    private static readonly Dictionary<string, (string Kind, string Mime)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = (AttachmentKinds.Image, "image/jpeg"),
        [".jpeg"] = (AttachmentKinds.Image, "image/jpeg"),
        [".png"] = (AttachmentKinds.Image, "image/png"),
        [".webp"] = (AttachmentKinds.Image, "image/webp"),
        [".pdf"] = (AttachmentKinds.Pdf, "application/pdf"),
        [".txt"] = (AttachmentKinds.Text, "text/plain"),
        [".csv"] = (AttachmentKinds.Csv, "text/csv"),
        [".xlsx"] = (AttachmentKinds.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        // voice recordings
        [".webm"] = (AttachmentKinds.Audio, "audio/webm"),
        [".ogg"] = (AttachmentKinds.Audio, "audio/ogg"),
        [".wav"] = (AttachmentKinds.Audio, "audio/wav"),
        [".mp3"] = (AttachmentKinds.Audio, "audio/mpeg"),
        [".m4a"] = (AttachmentKinds.Audio, "audio/mp4"),
        [".mp4"] = (AttachmentKinds.Audio, "audio/mp4"),
    };

    public static IReadOnlyCollection<string> AllowedExtensions => Allowed.Keys;

    /// <summary>Returns the detected type, or an error message.</summary>
    public static (FileTypeInfo? Info, string? Error) Detect(string fileName, string? declaredMime, ReadOnlySpan<byte> head, Stream fullContent)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!Allowed.TryGetValue(ext, out var allowed))
            return (null, $"File type '{(string.IsNullOrEmpty(ext) ? "(none)" : ext)}' is not supported. Allowed: images (jpg, png, webp), pdf, txt, csv, xlsx.");

        if (LooksExecutable(head))
            return (null, "Executable files are not allowed.");

        var ok = allowed.Kind switch
        {
            AttachmentKinds.Image => ext switch
            {
                ".jpg" or ".jpeg" => StartsWith(head, 0xFF, 0xD8, 0xFF),
                ".png" => StartsWith(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
                ".webp" => head.Length >= 12 && StartsWith(head, 0x52, 0x49, 0x46, 0x46) && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50,
                _ => false
            },
            AttachmentKinds.Pdf => IndexOf(head, "%PDF-"u8) is >= 0 and < 1024,
            AttachmentKinds.Excel => IsXlsx(fullContent),
            AttachmentKinds.Text or AttachmentKinds.Csv => IsText(fullContent),
            AttachmentKinds.Audio => IsAudio(ext, head),
            _ => false
        };
        if (!ok)
            return (null, $"The file content does not match its '{ext}' extension.");

        // Browser MIME is advisory, but an obviously conflicting claim is rejected.
        if (!string.IsNullOrWhiteSpace(declaredMime))
        {
            var d = declaredMime.ToLowerInvariant();
            var conflicting = allowed.Kind switch
            {
                AttachmentKinds.Image => !d.StartsWith("image/"),
                AttachmentKinds.Audio => !(d.StartsWith("audio/") || d.StartsWith("video/webm") || d == "application/octet-stream"),
                AttachmentKinds.Pdf => d is not ("application/pdf" or "application/octet-stream"),
                _ => d.Contains("executable") || d.Contains("x-msdownload") || d.Contains("javascript") || d.Contains("html")
            };
            if (conflicting) return (null, $"Declared content type '{declaredMime}' does not match the file.");
        }

        return (new FileTypeInfo(allowed.Kind, ext, allowed.Mime), null);
    }

    private static bool LooksExecutable(ReadOnlySpan<byte> head)
        => StartsWith(head, 0x4D, 0x5A)                 // MZ (Windows PE)
           || StartsWith(head, 0x7F, 0x45, 0x4C, 0x46)   // ELF
           || StartsWith(head, 0xCF, 0xFA, 0xED, 0xFE)   // Mach-O
           || StartsWith(head, 0x23, 0x21);              // #! script

    private static bool IsAudio(string ext, ReadOnlySpan<byte> head) => ext switch
    {
        ".webm" => StartsWith(head, 0x1A, 0x45, 0xDF, 0xA3),
        ".ogg" => StartsWith(head, 0x4F, 0x67, 0x67, 0x53),
        ".wav" => StartsWith(head, 0x52, 0x49, 0x46, 0x46),
        ".mp3" => StartsWith(head, 0x49, 0x44, 0x33) || (head.Length > 1 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
        ".m4a" or ".mp4" => head.Length > 8 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70, // ftyp
        _ => false
    };

    private static bool IsXlsx(Stream content)
    {
        try
        {
            content.Position = 0;
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            // Macro-enabled content (vbaProject.bin) is rejected even with an .xlsx extension.
            return zip.GetEntry("xl/workbook.xml") is not null && zip.GetEntry("xl/vbaProject.bin") is null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            content.Position = 0;
        }
    }

    private static bool IsText(Stream content)
    {
        content.Position = 0;
        var buffer = new byte[Math.Min(content.Length, 64 * 1024)];
        var read = content.Read(buffer, 0, buffer.Length);
        content.Position = 0;
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return false; // binary
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, TrimPartialUtf8(buffer, read));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return true; // legacy encodings (Windows-1252) are still text; decoded leniently later
        }
    }

    private static int TrimPartialUtf8(byte[] b, int len)
    {
        // Don't fail on a multi-byte character cut at the 64 KB boundary.
        var i = len;
        while (i > 0 && len - i < 4 && (b[i - 1] & 0xC0) == 0x80) i--;
        if (i > 0 && (b[i - 1] & 0xC0) == 0xC0) return i - 1;
        return len;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] sig) => data.Length >= sig.Length && data[..sig.Length].SequenceEqual(sig);

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value);
}

public static class FileNameSanitizer
{
    private static readonly Regex Unsafe = new(@"[^\p{L}\p{N}\.\-_ ()\[\]]+", RegexOptions.Compiled);

    /// <summary>Display-only name: no path parts, no control or special characters, bounded length.</summary>
    public static string Sanitize(string? fileName, string fallbackExtension)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty);
        name = Unsafe.Replace(name, "_").Trim(' ', '.', '_');
        if (name.Length == 0) name = "file" + fallbackExtension;
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }
}
