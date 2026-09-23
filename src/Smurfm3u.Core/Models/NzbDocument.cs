using System.Globalization;
using System.Xml.Linq;

namespace Smurfm3u.Core.Models;

/// <summary>What a document of ours was carrying: the entries it points at, under one name.</summary>
public sealed record NzbPayload(IReadOnlyList<long> ItemIds, string? ReleaseName);

/// <summary>
/// The .nzb file handed to Sonarr and Radarr when they grab a release.
/// There is no Usenet behind this service, so the document carries no real segments: it is a
/// signed-in-plain-sight pointer back to the playlist entries, which our SABnzbd endpoint reads
/// when the *arr app posts the same file back to us as a download.
/// <para>
/// A single episode or film points at one entry; a season pack points at one per episode, in
/// the order they should be transferred.
/// </para>
/// </summary>
public static class NzbDocument
{
    private static readonly XNamespace Ns = "http://www.newzbin.com/DTD/2003/nzb";

    /// <summary>Meta key holding the playlist entry id. Repeated once per entry.</summary>
    public const string ItemMetaType = "smurfm3u-item";

    public static string Build(long itemId, string releaseName, long sizeBytes) =>
        Build([itemId], [releaseName], releaseName, sizeBytes);

    /// <summary>
    /// A document covering several entries. Each gets a file element of its own named after
    /// the episode, because that name is what the *arr apps parse the finished folder against.
    /// </summary>
    public static string Build(
        IReadOnlyList<long> itemIds, IReadOnlyList<string> fileNames, string releaseName, long sizeBytes)
    {
        var posted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Declared once and divided, so the parts add up to the whole the client was shown.
        var perFile = Math.Max(1, sizeBytes / Math.Max(1, itemIds.Count));

        var head = new XElement(Ns + "head",
            new XElement(Ns + "meta", new XAttribute("type", "title"), releaseName),
            new XElement(Ns + "meta", new XAttribute("type", "size"),
                sizeBytes.ToString(CultureInfo.InvariantCulture)));

        foreach (var itemId in itemIds)
        {
            head.Add(new XElement(Ns + "meta",
                new XAttribute("type", ItemMetaType),
                itemId.ToString(CultureInfo.InvariantCulture)));
        }

        var nzb = new XElement(Ns + "nzb", head);

        for (var i = 0; i < itemIds.Count; i++)
        {
            var subject = i < fileNames.Count ? fileNames[i] : releaseName;

            nzb.Add(new XElement(Ns + "file",
                new XAttribute("poster", "smurfm3u@localhost"),
                new XAttribute("date", posted.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("subject", $"\"{subject}\" yEnc (1/1)"),
                new XElement(Ns + "groups", new XElement(Ns + "group", "alt.binaries.smurfm3u")),
                new XElement(Ns + "segments",
                    new XElement(Ns + "segment",
                        new XAttribute("bytes", perFile.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("number", "1"),
                        $"smurfm3u-{itemIds[i].ToString(CultureInfo.InvariantCulture)}"))));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), nzb);

        return document.Declaration + Environment.NewLine + document;
    }

    /// <summary>
    /// Recovers the playlist entry ids from a document we produced, in the order they were
    /// written. Returns false for anything else, including real Usenet nzb files a user might
    /// post by mistake.
    /// </summary>
    public static bool TryParse(string xml, out NzbPayload payload)
    {
        payload = new NzbPayload([], null);

        if (string.IsNullOrWhiteSpace(xml)) return false;

        try
        {
            var document = XDocument.Parse(xml);

            // Matched on local names so a document reserialized without our namespace still reads.
            var metas = document.Descendants().Where(e => e.Name.LocalName == "meta").ToList();

            var ids = metas
                .Where(e => (string?)e.Attribute("type") == ItemMetaType)
                .Select(e => long.TryParse(e.Value.Trim(), CultureInfo.InvariantCulture, out var id) ? id : -1)
                .Where(id => id >= 0)
                .ToList();

            // Older documents, and any reserialization that dropped the head, still carry the
            // ids in their segments. A grab in flight across an upgrade lands here.
            if (ids.Count == 0)
            {
                ids = document.Descendants()
                    .Where(e => e.Name.LocalName == "segment")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.StartsWith("smurfm3u-", StringComparison.Ordinal))
                    .Select(v => long.TryParse(v["smurfm3u-".Length..], CultureInfo.InvariantCulture, out var id) ? id : -1)
                    .Where(id => id >= 0)
                    .ToList();
            }

            if (ids.Count == 0) return false;

            var title = metas.FirstOrDefault(e => (string?)e.Attribute("type") == "title")?.Value.Trim();

            payload = new NzbPayload(ids, string.IsNullOrWhiteSpace(title) ? null : title);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
