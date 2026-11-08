using PdfPecker.Email.Mime;
using PdfPecker.Email.Msg;

namespace PdfPecker.Email;

public sealed partial class EmailMessage
{
    /// <summary>
    /// Puts the content of each winmail.dat (TNEF) part in its place: its attachments, and its body when that is
    /// better than the message's own. Outlook sends a plain text body next to a winmail.dat that holds the real one
    /// (HTML, or HTML in compressed RTF), so a TNEF body with HTML replaces a body without; otherwise the message's
    /// body stays and only the attachments come out. A part that is not valid TNEF stays an attachment.
    /// </summary>
    private static MimeEntity ExpandTnef(MimeEntity root, EmailLimits limits, CancellationToken cancellationToken)
    {
        var parents = new Dictionary<MimeEntity, MimeEntity>();
        var found = new List<MimeEntity>();
        FindTnef(root, null, parents, found);
        if (found.Count == 0) return root;
        var relaxed = new EmailLimits { MaxMessageBytes = int.MaxValue, MaxParts = limits.MaxParts * 3 + 3, MaxNestingDepth = limits.MaxNestingDepth * 3 + 3 };
        foreach (var part in found)
        {
            var data = part.DecodedBody;
            if (TnefObject.Read(data, cancellationToken) is not { } tnef) continue;

            var body = new List<BodyPart>();
            Collect(root, preferText: false, body, [], []);
            var hasHtml = body.Exists(p => p.Kind == BodyKind.Html);
            var hasText = body.Exists(p => p.Kind != BodyKind.Text || !string.IsNullOrWhiteSpace(p.Entity.DecodeText()));
            var useBody = OutlookMessage.HasBody(tnef) && (!hasText || OutlookMessage.HasHtmlBody(tnef) && !hasHtml);
            if (useBody)
            {
                foreach (var p in body)
                {
                    if (parents.TryGetValue(p.Entity, out var parent)) parent.Children.Remove(p.Entity);
                }
            }

            var (buffer, length) = OutlookMessage.ToMime(tnef, data.Length, limits, cancellationToken, headers: false, body: useBody);
            var content = MimeParser.Parse(buffer, length, relaxed, cancellationToken);
            if (parents.TryGetValue(part, out var container))
            {
                container.Children[container.Children.IndexOf(part)] = content;
                parents[content] = container;
                foreach (var child in content.Children) parents[child] = content;
            }
            else
            {
                // The whole message is a winmail.dat: its headers stay, and the content becomes its only part.
                var wrapper = new MimeEntity(root.Source, root.Headers, root.BodyStart, root.BodyEnd) { MediaType = "multipart", SubType = "mixed" };
                wrapper.Children.Add(content);
                root = wrapper;
            }
        }
        return root;
    }

    private static void FindTnef(MimeEntity entity, MimeEntity? parent, Dictionary<MimeEntity, MimeEntity> parents, List<MimeEntity> found)
    {
        if (parent is not null) parents[entity] = parent;
        if (entity.IsMultipart)
        {
            foreach (var child in entity.Children) FindTnef(child, entity, parents, found);
            return;
        }
        // An attached message keeps its own winmail.dat; only this message's parts are expanded.
        if (entity.MediaType == "message") return;
        var named = entity.FileName is { } name && (name.Equals("winmail.dat", StringComparison.OrdinalIgnoreCase) || name.Equals("win.dat", StringComparison.OrdinalIgnoreCase));
        if (entity.Is("application", "ms-tnef") || entity.Is("application", "vnd.ms-tnef") || named && entity.Is("application", "octet-stream"))
            found.Add(entity);
    }
}
