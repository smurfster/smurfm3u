using Smurfm3u.Core.Entities;

namespace Smurfm3u.Core.Parsing;

public sealed record ParsedTitle
{
    public MediaKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? EpisodeTitle { get; init; }

    /// <summary>The day an episode went out, when the name or the panel says. See <see cref="AirDates"/>.</summary>
    public DateOnly? AirDate { get; init; }

    /// <summary>Lowercased alphanumeric form of <see cref="Title"/>, used for matching queries.</summary>
    public string SearchTitle { get; init; } = string.Empty;
}
