using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.OutlookHost;

internal static partial class OutlookSignatureImages
{
    private const int MaximumHtmlCharacters = 1024 * 1024;
    private const int MaximumDepth = 48;
    private static readonly HashSet<string> SupportedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "title", "meta", "style", "body", "div", "p", "span", "br", "img", "b", "strong",
        "i", "em", "u", "font", "a", "small", "sub", "sup", "hr", "h1", "h2", "h3"
    };

    [GeneratedRegex(@"cid:(?<id>[a-z0-9._@+\-]{1,255})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ContentIdReference();

    internal static string? NormalizeContentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var id = value.Trim();
        if (id.Length > 2 && id[0] == '<' && id[^1] == '>') id = id[1..^1];
        return id.Length is > 0 and <= 255 && id.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '@' or '+' or '-') ? id : null;
    }

    internal static bool IsRaster(string contentType, ReadOnlySpan<byte> bytes) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        MediaMetadataSignature.TryDetect(bytes, out var format) &&
        format is MediaMetadataFormat.Jpeg or MediaMetadataFormat.Png or MediaMetadataFormat.Gif or
            MediaMetadataFormat.Bmp or MediaMetadataFormat.Tiff or MediaMetadataFormat.Webp;

    // Recognise only explicit, well-formed markup with stable HTML and XML ancestry.
    // All unsupported and ambiguous bodies retain their attachments.
    internal static IReadOnlyList<string> Find(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > MaximumHtmlCharacters ||
            !html.Contains("Signature", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("<!--[if", StringComparison.OrdinalIgnoreCase)) return [];

        XDocument document;
        try
        {
            using var source = new StringReader(html);
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumHtmlCharacters
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [];
        }

        if (document.Root is not { } root || !Is(root, "html")) return [];
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Any(element => !SupportedElements.Contains(element.Name.LocalName) || element.Name.Namespace != XNamespace.None ||
            element.Ancestors().Take(MaximumDepth).Count() == MaximumDepth ||
            element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                .GroupBy(attribute => attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            ((Is(element, "img") || Is(element, "meta") || Is(element, "br") || Is(element, "hr")) && element.FirstNode is not null) ||
            (Is(element, "p") && element.Descendants().Any(child => Is(child, "p") || Is(child, "div"))) ||
            (Is(element, "a") && element.Descendants().Any(child => Is(child, "a"))) ||
            (Is(element, "style") && (element.HasElements || element.Value.Contains('\\'))) ||
            element.Attributes().Any(attribute => AttributeIs(attribute, "style") && attribute.Value.Contains('\\'))))
        {
            return [];
        }

        var body = root.Elements().Where(element => Is(element, "body")).ToArray();
        var signatures = elements.Where(element => element.Attributes().Any(attribute =>
            AttributeIs(attribute, "id") && attribute.Value.Equals("Signature", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (body.Length != 1 || signatures.Length != 1 || !Is(signatures[0], "div") ||
            !signatures[0].Ancestors().Contains(body[0])) return [];

        var images = signatures[0].Descendants().Where(element => Is(element, "img"))
            .Select(element => element.Attributes().SingleOrDefault(attribute => AttributeIs(attribute, "src"))?.Value)
            .Where(value => value?.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) == true)
            .Select(value => NormalizeContentId(value![4..]))
            .OfType<string>()
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase);

        var literalReferences = CountReferences(html);
        var decodedReferences = CountReferences(Uri.UnescapeDataString(WebUtility.HtmlDecode(html))
            .Replace("\t", "").Replace("\r", "").Replace("\n", ""));
        return images.Where(group => literalReferences.GetValueOrDefault(group.Key) == group.Count() &&
                                      decodedReferences.GetValueOrDefault(group.Key) == group.Count())
            .Select(group => group.Key).ToArray();
    }

    private static Dictionary<string, int> CountReferences(string html) => ContentIdReference().Matches(html)
        .Select(match => match.Groups["id"].Value)
        .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static bool Is(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool AttributeIs(XAttribute attribute, string name) =>
        attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
}
