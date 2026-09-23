using Smurfm3u.Core.Parsing;

namespace Smurfm3u.Core.Models;

/// <summary>
/// A query as the client wrote it, with any season and episode taken out of the words.
/// <para>
/// The *arrs normally send those as their own parameters, but a query typed by hand - or by a
/// client that does not - carries them in the text: "lanterns s01e01". Left in, they are words
/// that have to appear in the title, and they never can: the title is stored with them
/// stripped out and the numbers in their own columns. The search then finds nothing, or finds
/// something only because the relaxed fallback rescued it.
/// </para>
/// </summary>
public sealed record SearchQuery(string? Text, int? Season, int? Episode)
{
    /// <summary>
    /// Pulls a season and episode out of the words when they are in there, leaving the rest as
    /// the title to match on. A query with none in it comes back untouched, so nothing that
    /// already worked starts behaving differently.
    /// </summary>
    public static SearchQuery Interpret(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchQuery(query, null, null);

        var parsed = ReleaseTitleParser.Parse(query);

        // Only when it actually found one. Anything else - a year, a resolution, a release
        // group - is left in the words, where the existing matching already handles it.
        if (parsed.Season is null && parsed.Episode is null) return new SearchQuery(query, null, null);

        // A parse that ate the whole query has misread it; better to search as written.
        return string.IsNullOrWhiteSpace(parsed.Title)
            ? new SearchQuery(query, null, null)
            : new SearchQuery(parsed.Title, parsed.Season, parsed.Episode);
    }
}
