namespace Smurfm3u.Core.Parsing;

/// <summary>
/// Streaming reader for extended M3U playlists. Tolerant by design: playlists in the wild
/// carry stray directives, BOMs and unquoted attribute values, and one bad line must not
/// cost us the rest of the file.
/// </summary>
public static class M3uParser
{
    private const char Quote = '"';

    public static async IAsyncEnumerable<M3uEntry> ParseAsync(
        TextReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? pendingName = null;
        var pendingDuration = 0;
        var pendingAttrs = NewAttrs();
        string? pendingGroup = null;

        while (await reader.ReadLineAsync(ct) is { } rawLine)
        {
            ct.ThrowIfCancellationRequested();

            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                (pendingDuration, pendingAttrs, pendingName) = ParseExtInf(line["#EXTINF:".Length..]);
                if (pendingGroup is not null && !pendingAttrs.ContainsKey("group-title"))
                    pendingAttrs["group-title"] = pendingGroup;
                continue;
            }

            // #EXTGRP applies to every entry that follows, until another one replaces it.
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                pendingGroup = line["#EXTGRP:".Length..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            if (pendingName is null)
                continue;

            yield return new M3uEntry
            {
                DisplayName = pendingName,
                Url = line,
                DurationSeconds = pendingDuration,
                Attributes = pendingAttrs
            };

            pendingName = null;
            pendingDuration = 0;
            pendingAttrs = NewAttrs();
        }
    }

    private static Dictionary<string, string> NewAttrs() => new(StringComparer.OrdinalIgnoreCase);

    private static (int Duration, Dictionary<string, string> Attributes, string Name) ParseExtInf(string body)
    {
        // Layout is: <duration> [key="value" ...],<display name>
        // Display names routinely contain commas, so split on the first comma outside quotes.
        var inQuotes = false;
        var split = -1;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == Quote) inQuotes = !inQuotes;
            else if (body[i] == ',' && !inQuotes) { split = i; break; }
        }

        var head = split >= 0 ? body[..split] : body;
        var name = split >= 0 ? body[(split + 1)..].Trim() : string.Empty;

        var attrs = ParseAttributes(head);

        var durationToken = head.AsSpan().Trim();
        var space = durationToken.IndexOf(' ');
        if (space >= 0) durationToken = durationToken[..space];
        _ = double.TryParse(durationToken, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var duration);

        // Playlists use -1 (occasionally 0) to mean "no known runtime".
        var seconds = duration > 0 ? (int)Math.Round(duration) : 0;

        if (name.Length == 0 && attrs.TryGetValue("tvg-name", out var tvgName))
            name = tvgName;

        return (seconds, attrs, name);
    }

    /// <summary>Scans <c>key="value"</c> / <c>key=value</c> pairs out of the EXTINF header.</summary>
    private static Dictionary<string, string> ParseAttributes(string head)
    {
        var attrs = NewAttrs();
        var i = 0;

        while (i < head.Length)
        {
            while (i < head.Length && head[i] != '=') i++;
            if (i >= head.Length) break;

            var eq = i;
            var keyEnd = eq;
            while (keyEnd > 0 && char.IsWhiteSpace(head[keyEnd - 1])) keyEnd--;

            var keyStart = keyEnd;
            while (keyStart > 0 && (char.IsLetterOrDigit(head[keyStart - 1]) || head[keyStart - 1] is '-' or '_'))
                keyStart--;

            i = eq + 1;
            while (i < head.Length && char.IsWhiteSpace(head[i])) i++;

            string value;
            if (i < head.Length && head[i] == Quote)
            {
                var valStart = ++i;
                while (i < head.Length && head[i] != Quote) i++;
                value = head[valStart..Math.Min(i, head.Length)];
                if (i < head.Length) i++;
            }
            else
            {
                var valStart = i;
                while (i < head.Length && !char.IsWhiteSpace(head[i])) i++;
                value = head[valStart..i];
            }

            if (keyEnd > keyStart)
                attrs[head[keyStart..keyEnd]] = value.Trim();
        }

        return attrs;
    }
}
