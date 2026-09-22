using System.Reflection;

namespace Smurfm3u.Core;

/// <summary>
/// What version this is, read back from the assembly rather than written out a second time,
/// so the number in the UI is always the number that was built. It is set in
/// Directory.Build.props at the root of the repository.
/// </summary>
public static class AppVersion
{
    private static readonly (string Version, string? Commit) Built = Split(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    /// <summary>The version on its own, e.g. "1.0.0".</summary>
    public static string Display => Built.Version;

    /// <summary>
    /// The commit it was built from, shortened, or null when it was not built from a checkout.
    /// Worth having on a self-hosted service, where "which build is this" is otherwise guesswork.
    /// </summary>
    public static string? Commit => Built.Commit;

    /// <summary>
    /// The build appends the commit to the informational version as "1.0.0+abc123…". Useful to
    /// keep, but not to read as part of the version, so the two come back apart.
    /// </summary>
    public static (string Version, string? Commit) Split(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("0.0.0", null);

        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational.Trim(), null);

        var commit = informational[(plus + 1)..].Trim();

        return (
            informational[..plus].Trim(),
            commit.Length == 0 ? null : commit[..Math.Min(7, commit.Length)]);
    }
}
