namespace Smurfm3u.Core.Models;

/// <summary>
/// The arithmetic behind a paged grid: which rows a page covers, how many pages there are,
/// and where a requested page actually lands. Pages are one-based because they are shown to
/// a person rather than passed to Skip.
/// </summary>
public readonly record struct PageRange(int Page, int PageSize, int TotalCount)
{
    /// <summary>Always at least one, so an empty grid still reads as "page 1 of 1".</summary>
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize)));

    /// <summary>Rows to skip to reach this page.</summary>
    public int Skip => (Clamped - 1) * Math.Max(1, PageSize);

    /// <summary>The page actually in effect, once an out-of-range request is pulled back.</summary>
    public int Clamped => Math.Clamp(Page, 1, PageCount);

    /// <summary>One-based index of the first row shown, or 0 when there is nothing to show.</summary>
    public int First => TotalCount == 0 ? 0 : Skip + 1;

    /// <summary>One-based index of the last row shown, or 0 when there is nothing to show.</summary>
    public int Last => TotalCount == 0 ? 0 : Math.Min(TotalCount, Skip + Math.Max(1, PageSize));

    public bool HasPrevious => TotalCount > 0 && Clamped > 1;

    public bool HasNext => TotalCount > 0 && Clamped < PageCount;

    /// <summary>"1-50 of 1,204", or a plain phrase when the grid is empty.</summary>
    public string Label => TotalCount == 0 ? "Nothing to show" : $"{First:N0}-{Last:N0} of {TotalCount:N0}";
}
