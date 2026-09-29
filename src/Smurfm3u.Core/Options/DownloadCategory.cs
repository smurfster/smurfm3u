using Smurfm3u.Core.Parsing;

namespace Smurfm3u.Core.Options;

/// <summary>
/// A SABnzbd category: the name a client files a grab under, where that grab's finished
/// folder goes, and the priority it gets when the client does not name one.
/// </summary>
public class DownloadCategory
{
    /// <summary>The name of SAB's catch-all category, which every list has exactly once.</summary>
    public const string DefaultName = "*";

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where finished grabs go: relative to the complete directory, or an absolute path.
    /// Blank means a folder named after the category, or the complete directory itself for
    /// the default category - both as SABnzbd lays them out.
    /// </summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// A <see cref="SabPriority"/> value. <see cref="SabPriority.Default"/> defers to the
    /// default category, which is the one place it is not allowed.
    /// </summary>
    public int Priority { get; set; } = SabPriority.Default;

    public bool IsDefault => Name == DefaultName;
}

/// <summary>SABnzbd's priority numbers. A higher number runs sooner.</summary>
public static class SabPriority
{
    /// <summary>"Use the category's priority." What Sonarr and Radarr send unless told otherwise.</summary>
    public const int Default = -100;

    /// <summary>Queue the grab paused rather than at a priority of its own.</summary>
    public const int Paused = -2;

    public const int Low = -1;
    public const int Normal = 0;
    public const int High = 1;
    public const int Force = 2;

    /// <summary>The priorities a category or a grab can be given, highest first.</summary>
    public static readonly IReadOnlyList<(int Value, string Name)> Choices =
    [
        (Force, "Force"),
        (High, "High"),
        (Normal, "Normal"),
        (Low, "Low")
    ];

    public static string Name(int priority) => priority switch
    {
        >= Force => "Force",
        High => "High",
        Normal => "Normal",
        _ => "Low"
    };
}

/// <summary>How a grab's requested category and priority turn into a folder and a queue position.</summary>
public static class DownloadCategories
{
    /// <summary>The category Prowlarr's SABnzbd download client is set to unless changed.</summary>
    public const string ProwlarrName = "prowlarr";

    /// <summary>
    /// Puts the list into the shape everything else relies on: the default category first
    /// and only once, with a priority of its own, and no blank, repeated or upper-case names.
    /// A list that was never saved - a fresh install, or settings from before categories were
    /// editable - is built from the TV and movie category names, which is what those installs
    /// reported to clients, plus the one Prowlarr's download client asks for out of the box.
    /// </summary>
    public static void Normalise(ServiceSettings settings)
    {
        var existing = settings.Categories;

        if (existing.Count == 0)
        {
            existing.Add(new DownloadCategory { Name = settings.TvCategory });
            existing.Add(new DownloadCategory { Name = settings.MovieCategory });

            // Prowlarr's SABnzbd client defaults to this category and its Test fails when the
            // category is not listed, so it is there before anyone has to go looking.
            existing.Add(new DownloadCategory { Name = ProwlarrName });
        }

        var fallback = existing.FirstOrDefault(x => x.Name?.Trim() == DownloadCategory.DefaultName)
                       ?? new DownloadCategory { Name = DownloadCategory.DefaultName, Priority = SabPriority.Normal };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DownloadCategory.DefaultName };
        var tidy = new List<DownloadCategory> { fallback };

        foreach (var category in existing)
        {
            if (ReferenceEquals(category, fallback)) continue;

            // Lower case, as SABnzbd keeps them: the clients compare the category they read
            // back against the one they sent, and a real SAB would never answer "TV".
            var name = category.Name?.Trim().ToLowerInvariant() ?? string.Empty;
            if (name.Length == 0 || !seen.Add(name)) continue;

            category.Name = name;
            category.Folder = category.Folder?.Trim() ?? string.Empty;
            category.Priority = Clamp(category.Priority, allowDefault: true);
            tidy.Add(category);
        }

        fallback.Name = DownloadCategory.DefaultName;
        fallback.Folder = fallback.Folder?.Trim() ?? string.Empty;
        fallback.Priority = Clamp(fallback.Priority, allowDefault: false);

        settings.Categories = tidy;

        // The Search page files its grabs under these, so they have to name a real category.
        settings.TvCategory = Find(settings, settings.TvCategory)?.Name ?? DownloadCategory.DefaultName;
        settings.MovieCategory = Find(settings, settings.MovieCategory)?.Name ?? DownloadCategory.DefaultName;
    }

    /// <summary>The category with this name, ignoring case as SABnzbd does, or null.</summary>
    public static DownloadCategory? Find(ServiceSettings settings, string? name)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;

        return settings.Categories.FirstOrDefault(
            x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The default category; <see cref="Normalise"/> guarantees there is one.</summary>
    public static DownloadCategory Fallback(ServiceSettings settings) =>
        settings.Categories.FirstOrDefault(x => x.IsDefault)
        ?? new DownloadCategory { Name = DownloadCategory.DefaultName, Priority = SabPriority.Normal };

    /// <summary>
    /// The category a grab is filed under. A name nobody configured lands in the default
    /// category, as it does in SABnzbd, rather than inventing a folder of its own.
    /// </summary>
    public static DownloadCategory Resolve(ServiceSettings settings, string? name) =>
        Find(settings, name) ?? Fallback(settings);

    /// <summary>
    /// The priority a grab runs at. Asking for "Default" takes the category's, and a category
    /// set to "Default" takes the default category's.
    /// </summary>
    public static int EffectivePriority(ServiceSettings settings, DownloadCategory category, int? requested)
    {
        if (requested is { } asked and not SabPriority.Default)
            return Clamp(asked, allowDefault: false);

        return category.Priority != SabPriority.Default
            ? category.Priority
            : Fallback(settings).Priority;
    }

    /// <summary>
    /// The folder a category's finished grabs are moved into, on this machine. Folder names
    /// are cleaned segment by segment, so a relative folder can still nest (<c>media/tv</c>).
    /// </summary>
    public static string OutputFolder(DownloadCategory category, string completePath)
    {
        var folder = category.Folder.Trim();

        if (folder.Length == 0)
            return category.IsDefault
                ? completePath
                : Path.Combine(completePath, ReleaseNameBuilder.SanitizePathSegment(category.Name));

        if (Path.IsPathRooted(folder))
            return folder;

        var segments = folder
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x is not ("." or ".."))
            .Select(ReleaseNameBuilder.SanitizePathSegment)
            .Where(x => x.Length > 0);

        return Path.Combine([completePath, .. segments]);
    }

    /// <summary>
    /// The folder as SABnzbd's get_config reports it: relative folders stay relative, since
    /// clients join them to complete_dir themselves, and absolute ones go out through the
    /// path mappings like every other path.
    /// </summary>
    public static string ReportedFolder(DownloadCategory category, IReadOnlyCollection<PathMapping> mappings)
    {
        var folder = category.Folder.Trim();

        if (folder.Length == 0)
            return category.IsDefault ? string.Empty : category.Name;

        return Path.IsPathRooted(folder) ? PathMapper.Apply(folder, mappings) : folder;
    }

    private static int Clamp(int priority, bool allowDefault) => priority switch
    {
        SabPriority.Default when allowDefault => SabPriority.Default,
        SabPriority.Default => SabPriority.Normal,
        > SabPriority.Force => SabPriority.Force,
        < SabPriority.Low => SabPriority.Low,
        _ => priority
    };
}
