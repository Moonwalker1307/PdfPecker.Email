namespace PdfPecker.Email;

/// <summary>
/// The words of the header block above the message body. The defaults are English; set them for another language,
/// for example <c>new EmailLabels { From = "Fra", Date = "Sendt", To = "Til", Attachments = "Vedhæftede filer" }</c>.
/// </summary>
public sealed class EmailLabels
{
    /// <summary>Default: "From".</summary>
    public string From { get; set; } = "From";

    /// <summary>Default: "Sent".</summary>
    public string Date { get; set; } = "Sent";

    /// <summary>Default: "To".</summary>
    public string To { get; set; } = "To";

    /// <summary>Default: "Cc".</summary>
    public string Cc { get; set; } = "Cc";

    /// <summary>Default: "Bcc".</summary>
    public string Bcc { get; set; } = "Bcc";

    /// <summary>Default: "Reply-To". Shown only when the reply address differs from the sender's.</summary>
    public string ReplyTo { get; set; } = "Reply-To";

    /// <summary>Default: "Attachments".</summary>
    public string Attachments { get; set; } = "Attachments";

    /// <summary>The heading and PDF title of a message without a subject. Default: "(no subject)".</summary>
    public string NoSubject { get; set; } = "(no subject)";
}
