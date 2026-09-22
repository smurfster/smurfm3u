namespace Smurfm3u.Core.Parsing;

/// <summary>
/// One entry on its way into the database, whatever it came from. A playlist file and a panel
/// API disagree about almost everything up to this point and about nothing after it, so the
/// reconciling, batching and retiring in the refresh only has to know this shape.
/// </summary>
/// <param name="Parsed">
/// Already worked out, or null to let the title parser do it. A panel states the season and
/// episode outright, which is better than anything that can be read back out of a name; a
/// playlist file states nothing, so there it is always null.
/// </param>
/// <param name="SeriesId">
/// Which series an episode belongs to on the panel, so a later refresh can keep the whole
/// series alive without asking about it again. Null for anything that is not a panel episode.
/// </param>
/// <param name="SeriesLastModified">What the panel said that series was last changed at.</param>
public sealed record IngestCandidate(
    M3uEntry Entry,
    VodVerdict Verdict,
    ParsedTitle? Parsed = null,
    string? SeriesId = null,
    long? SeriesLastModified = null);
