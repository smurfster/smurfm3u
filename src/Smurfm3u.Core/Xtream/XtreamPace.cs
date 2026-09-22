namespace Smurfm3u.Core.Xtream;

/// <summary>
/// How many series to ask a panel about at once, adjusted by how it is answering.
/// <para>
/// Dropping on a refusal is the easy half. The half that matters on a large panel is climbing
/// back: one refusal early in a walk of thirty thousand series should not decide the pace of
/// the remaining twenty-nine thousand, which is the difference between an hour and five.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe by itself. The caller holds one per refresh and guards it, because the
/// requests reporting back are in flight together.
/// </remarks>
public sealed class XtreamPace
{
    /// <summary>Clean answers before the first attempt at going faster again.</summary>
    public const int DefaultStepUpAfter = 25;

    /// <summary>
    /// A ceiling on patience. Past this it has clearly settled at a pace the panel likes, and
    /// climbing again would only buy another refusal.
    /// </summary>
    private const int MostPatient = 800;

    private int stepUpAfter;
    private int clean;

    public XtreamPace(int most, int stepUpAfter = DefaultStepUpAfter)
    {
        Most = Math.Max(1, most);
        Batch = Most;
        this.stepUpAfter = Math.Max(1, stepUpAfter);
    }

    /// <summary>The most it will ever ask for at once.</summary>
    public int Most { get; }

    /// <summary>What to ask for now.</summary>
    public int Batch { get; private set; }

    /// <summary>
    /// The panel pushed back: halve, and want twice as much quiet before trying to climb.
    /// Returns whether the pace actually changed, so a caller only says so when it did.
    /// </summary>
    public bool Refused()
    {
        clean = 0;

        // Each refusal makes it slower to get its confidence back, so a panel with a hard
        // limit settles at that limit instead of oscillating around it for the whole walk.
        stepUpAfter = Math.Min(MostPatient, stepUpAfter * 2);

        if (Batch == 1) return false;

        Batch = Math.Max(1, Batch / 2);
        return true;
    }

    /// <summary>
    /// A clean answer. Once enough have come in a row, try one more at a time.
    /// Returns whether the pace changed.
    /// </summary>
    public bool Answered()
    {
        if (Batch >= Most) return false;
        if (++clean < stepUpAfter) return false;

        clean = 0;
        Batch++;
        return true;
    }
}
