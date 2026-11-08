namespace PdfPecker.Email;

/// <summary>A mailbox from an address header: the display name, if any, and the address.</summary>
public sealed class EmailAddress
{
    /// <summary>A mailbox with a display name (or null) and an address.</summary>
    public EmailAddress(string? name, string address)
    {
        Name = name;
        Address = address ?? string.Empty;
    }

    /// <summary>The display name, decoded, such as "Ann Example"; null when the header gives none.</summary>
    public string? Name { get; }

    /// <summary>The address, such as "ann@example.com". It can be empty for a damaged header that has only a name.</summary>
    public string Address { get; }

    /// <summary>"Name &lt;address&gt;", or the address alone when there is no name.</summary>
    public override string ToString()
        => Name is null ? Address : Address.Length == 0 ? Name : $"{Name} <{Address}>";
}
