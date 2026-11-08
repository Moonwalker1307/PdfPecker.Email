namespace PdfPecker.Email;

/// <summary>
/// Limits that keep a hostile or broken message from using unbounded time or memory. A message over a limit fails
/// with <see cref="PdfLimitException"/>. The defaults are far above what real mail needs.
/// </summary>
public sealed class EmailLimits
{
    /// <summary>The largest message read, in bytes. Default: 100 MB.</summary>
    public int MaxMessageBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>The most MIME parts in one message, counting every nested part. Default: 10,000.</summary>
    public int MaxParts { get; set; } = 10_000;

    /// <summary>How deeply multiparts and attached messages may nest. Default: 64.</summary>
    public int MaxNestingDepth { get; set; } = 64;

    internal static readonly EmailLimits Default = new();
}
