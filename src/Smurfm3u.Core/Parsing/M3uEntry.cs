namespace Smurfm3u.Core.Parsing;

/// <summary>A raw playlist entry, straight out of the file with nothing interpreted yet.</summary>
public sealed class M3uEntry
{
    public string DisplayName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int DurationSeconds { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? GroupTitle => Get("group-title");
    public string? TvgId => Get("tvg-id");
    public string? TvgName => Get("tvg-name");
    public string? TvgLogo => Get("tvg-logo");

    public string? Get(string key) => Attributes.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}
