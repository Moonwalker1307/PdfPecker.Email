using System.Text;
using PdfPecker.Email.Mime;

namespace PdfPecker.Email;

/// <summary>
/// Makes attachment file names safe to save and to embed: the name from the part, else from an attached message's
/// subject, else from the part's type; without folders, control characters or characters Windows forbids; unique.
/// </summary>
internal static class FileNames
{
    private const int MaxLength = 150;

    internal static string Make(MimeEntity part, int number, HashSet<string> used)
    {
        var name = part.FileName;
        if (name is null && part.MediaType == "message" && part.Children.Count == 1)
        {
            var subject = part.Children[0].Header("Subject");
            if (!string.IsNullOrWhiteSpace(subject)) name = HeaderValues.DecodeWords(subject) + ".eml";
        }
        name = Clean(name);
        if (name.Length == 0) name = "attachment-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + Extension(part);
        else if (part.MediaType == "message" && !Path.HasExtension(name)) name += ".eml";
        var unique = name;
        for (var n = 2; !used.Add(unique); n++)
        {
            var ext = Path.GetExtension(name);
            unique = name[..^ext.Length] + " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + ext;
        }
        return unique;
    }

    internal static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        // Only the last path segment is a file name; senders on Windows include the folder.
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') sb.Append('_');
            else sb.Append(c);
        }
        var clean = sb.ToString().Trim().Trim('.').Trim();
        if (clean.Length > MaxLength)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 20) ext = string.Empty;
            clean = clean[..(MaxLength - ext.Length)].TrimEnd() + ext;
            // Never cut a surrogate pair in half.
            if (clean.Length > 0 && char.IsHighSurrogate(clean[^(ext.Length + 1)..][0])) clean = clean.Remove(clean.Length - ext.Length - 1, 1);
        }
        return clean;
    }

    private static string Extension(MimeEntity part) => part.ContentType switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/svg+xml" => ".svg",
        "text/plain" => ".txt",
        "text/html" => ".html",
        "text/calendar" => ".ics",
        "text/csv" => ".csv",
        "text/xml" or "application/xml" => ".xml",
        "application/json" => ".json",
        "application/zip" => ".zip",
        "application/ms-tnef" or "application/vnd.ms-tnef" => ".dat",
        "application/pkcs7-signature" or "application/x-pkcs7-signature" => ".p7s",
        "application/pkcs7-mime" or "application/x-pkcs7-mime" => ".p7m",
        "application/pgp-signature" => ".asc",
        "message/rfc822" or "message/global" => ".eml",
        "message/delivery-status" or "message/global-delivery-status" => ".txt",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        _ => ".bin",
    };
}
