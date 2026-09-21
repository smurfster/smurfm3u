namespace Smurfm3u.Core.Models;

/// <summary>
/// How close a title is to a query whose words do not all appear in it. Used only once a
/// strict search has come back empty, to answer with the nearest entries rather than nothing.
/// </summary>
public static class SearchRelaxation
{
    /// <summary>
    /// Each word that appears scores its own length. Length stands in for rarity, so a long
    /// distinctive word such as "crunchlabs" counts for more than a short common one such as
    /// "mark", without needing statistics over the whole playlist.
    /// </summary>
    public static int Score(string? searchTitle, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrEmpty(searchTitle) || tokens.Count == 0) return 0;

        var total = 0;
        foreach (var token in tokens)
        {
            if (searchTitle.Contains(token, StringComparison.Ordinal))
                total += token.Length;
        }

        return total;
    }

    /// <summary>
    /// Keeps only the entries that match best, dropping weaker ones that merely share a word
    /// with the query. Entries that score nothing are never kept.
    /// </summary>
    public static IReadOnlyList<T> BestTier<T>(
        IReadOnlyList<T> candidates, Func<T, string?> titleOf, IReadOnlyList<string> tokens)
    {
        if (candidates.Count == 0) return candidates;

        var scored = candidates.Select(c => (Item: c, Score: Score(titleOf(c), tokens))).ToList();
        var best = scored.Max(x => x.Score);

        if (best == 0) return [];

        return scored.Where(x => x.Score == best).Select(x => x.Item).ToList();
    }
}
