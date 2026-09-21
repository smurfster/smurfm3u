using System.Globalization;
using System.Xml.Linq;

namespace Smurfm3u.App.Services;

/// <summary>
/// The .nzb file handed to Sonarr and Radarr when they grab a release.
/// There is no Usenet behind this service, so the document carries no real segments: it is a
/// signed-in-plain-sight pointer back to the playlist entry, which our SABnzbd endpoint reads
/// when the *arr app posts the same file back to us as a download.
/// </summary>
public static class NzbDocument
{
    private static readonly XNamespace Ns = "http://www.newzbin.com/DTD/2003/nzb";

    /// <summary>Meta key holding the playlist entry id.</summary>
    public const string ItemMetaType = "smurfm3u-item";

    public static string Build(long itemId, string releaseName, long sizeBytes)
    {
        var posted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "nzb",
                new XElement(Ns + "head",
                    new XElement(Ns + "meta", new XAttribute("type", ItemMetaType),
                        itemId.ToString(CultureInfo.InvariantCulture)),
                    new XElement(Ns + "meta", new XAttribute("type", "title"), releaseName),
                    new XElement(Ns + "meta", new XAttribute("type", "size"),
                        sizeBytes.ToString(CultureInfo.InvariantCulture))),
                new XElement(Ns + "file",
                    new XAttribute("poster", "smurfm3u@localhost"),
                    new XAttribute("date", posted.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("subject", $"\"{releaseName}\" yEnc (1/1)"),
                    new XElement(Ns + "groups", new XElement(Ns + "group", "alt.binaries.smurfm3u")),
                    new XElement(Ns + "segments",
                        new XElement(Ns + "segment",
                            new XAttribute("bytes", Math.Max(1, sizeBytes).ToString(CultureInfo.InvariantCulture)),
                            new XAttribute("number", "1"),
                            $"smurfm3u-{itemId}")))));

        return document.Declaration + Environment.NewLine + document;
    }

    /// <summary>
    /// Recovers the playlist entry id from a document we produced. Returns false for anything
    /// else, including real Usenet nzb files a user might post by mistake.
    /// </summary>
    public static bool TryParseItemId(string xml, out long itemId)
    {
        itemId = 0;

        if (string.IsNullOrWhiteSpace(xml)) return false;

        try
        {
            var document = XDocument.Parse(xml);

            // Match on local names so a document reserialized without our namespace still reads.
            var meta = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "meta"
                                     && (string?)e.Attribute("type") == ItemMetaType);

            if (meta is not null && long.TryParse(meta.Value.Trim(), out itemId))
                return true;

            var segment = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "segment");
            var value = segment?.Value.Trim();

            if (value is not null && value.StartsWith("smurfm3u-", StringComparison.Ordinal))
                return long.TryParse(value["smurfm3u-".Length..], out itemId);

            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
