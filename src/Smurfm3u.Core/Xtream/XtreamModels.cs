using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smurfm3u.Core.Xtream;

/// <summary>
/// Panels are loose about JSON types: the same field comes back as 12345 from one and "12345"
/// from the next, and the same panel is not always consistent with itself. Reading both costs
/// a converter and saves a whole class of refresh that dies on one provider and not another.
/// </summary>
public sealed class LooseStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var whole)
                ? whole.ToString()
                : reader.GetDouble().ToString("0.####"),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>As <see cref="LooseIntConverter"/>, for values too big for an int - unix stamps.</summary>
public sealed class LooseLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var number) ? number : null,
            JsonTokenType.String => long.TryParse(reader.GetString(), out var parsed) ? parsed : null,
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>As <see cref="LooseStringConverter"/>, for the numbers. Blank and "n/a" read as null.</summary>
public sealed class LooseIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? number : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return int.TryParse(text, out var parsed) ? parsed : null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>
/// Reads a nested object, and treats anything that is not one as absent.
/// <para>
/// PHP has one type for lists and maps, and encodes an empty one as <c>[]</c> rather than
/// <c>{}</c>. Panels are written in PHP, so any field documented as an object arrives as an
/// empty array whenever it holds nothing: the episode map of a series with no episodes, and
/// the info block of an episode the panel knows no runtime for. Refusing those costs a whole
/// series, which is most of a catalogue on a panel that is sparse about runtimes.
/// </para>
/// </summary>
/// <remarks>
/// The attribute goes on the property rather than the type, so the deserialise below resolves
/// the ordinary converter for <typeparamref name="T"/> and does not call back into this one.
/// </remarks>
public class LooseObjectConverter<T> : JsonConverter<T?> where T : class
{
    /// <summary>What a non-object reads as. Null unless a subclass wants an empty one.</summary>
    protected virtual T? Absent => null;

    public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return Absent;
        }

        return JsonSerializer.Deserialize<T>(ref reader, options) ?? Absent;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>
/// The episode map, keyed by season number. Empty rather than null when the panel sends
/// nothing, because every caller walks it and none of them should have to check first.
/// <para>
/// A non-empty array is read as a map keyed by position, because that is what it was before
/// PHP encoded it. <c>json_encode</c> writes a list rather than an object whenever an array's
/// keys are exactly 0, 1, 2 and so on in order &mdash; so a series whose seasons start at a
/// season 0 of specials and run without a gap arrives as <c>[[...],[...]]</c>, while the same
/// panel sends <c>{"1":[...],"2":[...]}</c> for the series next to it. Index is the key those
/// seasons had, which is exactly what a panel that leaves the season off the episode needs.
/// </para>
/// </summary>
/// <remarks>
/// Treating that array as "no episodes", which is what an object-only reading does, loses the
/// whole series silently: a search finds the series, fetches nothing, records the fetch as
/// done, and answers with near matches forever.
/// </remarks>
public sealed class EpisodeMapConverter : LooseObjectConverter<Dictionary<string, List<XtreamEpisode>>>
{
    protected override Dictionary<string, List<XtreamEpisode>> Absent => [];

    public override Dictionary<string, List<XtreamEpisode>>? Read(
        ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return base.Read(ref reader, type, options);

        // The reader is a struct, so this is a rewind point. A panel that sends an array of
        // something other than episode lists should cost this one field, not the series.
        var before = reader;

        try
        {
            var seasons = JsonSerializer.Deserialize<List<List<XtreamEpisode>?>>(ref reader, options) ?? [];

            var map = new Dictionary<string, List<XtreamEpisode>>(seasons.Count);

            for (var i = 0; i < seasons.Count; i++)
                if (seasons[i] is { Count: > 0 } episodes)
                    map[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = episodes;

            return map;
        }
        catch (JsonException)
        {
            reader = before;
            reader.Skip();

            return Absent;
        }
    }
}

/// <summary>What the panel says about the account, used to fail a test with a real reason.</summary>
public sealed class XtreamAuth
{
    [JsonPropertyName("user_info")]
    [JsonConverter(typeof(LooseObjectConverter<XtreamUserInfo>))]
    public XtreamUserInfo? UserInfo { get; init; }
}

public sealed class XtreamUserInfo
{
    [JsonPropertyName("auth")][JsonConverter(typeof(LooseIntConverter))] public int? Auth { get; init; }
    [JsonPropertyName("status")][JsonConverter(typeof(LooseStringConverter))] public string? Status { get; init; }
    [JsonPropertyName("exp_date")][JsonConverter(typeof(LooseStringConverter))] public string? Expires { get; init; }
    [JsonPropertyName("max_connections")][JsonConverter(typeof(LooseStringConverter))] public string? MaxConnections { get; init; }

    /// <summary>Panels answer 200 with auth 0 for a bad login rather than a 401.</summary>
    public bool IsAuthenticated => Auth is 1;

    /// <summary>"Active" is the good one; "Expired" and "Banned" are the ones worth reporting.</summary>
    public bool IsActive => string.IsNullOrWhiteSpace(Status)
                            || Status.Equals("Active", StringComparison.OrdinalIgnoreCase);
}

public sealed class XtreamCategory
{
    [JsonPropertyName("category_id")][JsonConverter(typeof(LooseStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("category_name")][JsonConverter(typeof(LooseStringConverter))] public string? Name { get; init; }
}

public sealed class XtreamVodStream
{
    [JsonPropertyName("stream_id")][JsonConverter(typeof(LooseStringConverter))] public string? StreamId { get; init; }
    [JsonPropertyName("name")][JsonConverter(typeof(LooseStringConverter))] public string? Name { get; init; }
    [JsonPropertyName("stream_icon")][JsonConverter(typeof(LooseStringConverter))] public string? Icon { get; init; }
    [JsonPropertyName("category_id")][JsonConverter(typeof(LooseStringConverter))] public string? CategoryId { get; init; }
    [JsonPropertyName("container_extension")][JsonConverter(typeof(LooseStringConverter))] public string? ContainerExtension { get; init; }
}

public sealed class XtreamSeries
{
    [JsonPropertyName("series_id")][JsonConverter(typeof(LooseStringConverter))] public string? SeriesId { get; init; }
    [JsonPropertyName("name")][JsonConverter(typeof(LooseStringConverter))] public string? Name { get; init; }
    [JsonPropertyName("cover")][JsonConverter(typeof(LooseStringConverter))] public string? Cover { get; init; }
    [JsonPropertyName("category_id")][JsonConverter(typeof(LooseStringConverter))] public string? CategoryId { get; init; }
    [JsonPropertyName("releaseDate")][JsonConverter(typeof(LooseStringConverter))] public string? ReleaseDate { get; init; }

    /// <summary>
    /// When the panel says the series last changed. The whole point of reading it is to avoid
    /// asking for an episode list that cannot have changed since the last refresh.
    /// </summary>
    [JsonPropertyName("last_modified")]
    [JsonConverter(typeof(LooseLongConverter))]
    public long? LastModified { get; init; }
}

public sealed class XtreamSeriesInfo
{
    [JsonPropertyName("episodes")]
    [JsonConverter(typeof(EpisodeMapConverter))]
    public Dictionary<string, List<XtreamEpisode>> Episodes { get; init; } = [];
}

public sealed class XtreamEpisode
{
    [JsonPropertyName("id")][JsonConverter(typeof(LooseStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("title")][JsonConverter(typeof(LooseStringConverter))] public string? Title { get; init; }
    [JsonPropertyName("season")][JsonConverter(typeof(LooseIntConverter))] public int? Season { get; init; }
    [JsonPropertyName("episode_num")][JsonConverter(typeof(LooseIntConverter))] public int? EpisodeNumber { get; init; }
    [JsonPropertyName("container_extension")][JsonConverter(typeof(LooseStringConverter))] public string? ContainerExtension { get; init; }
    [JsonPropertyName("info")]
    [JsonConverter(typeof(LooseObjectConverter<XtreamEpisodeInfo>))]
    public XtreamEpisodeInfo? Info { get; init; }
}

public sealed class XtreamEpisodeInfo
{
    [JsonPropertyName("duration_secs")][JsonConverter(typeof(LooseIntConverter))] public int? DurationSeconds { get; init; }

    // Panels name the day an episode aired differently; whichever is there is used.
    [JsonPropertyName("air_date")][JsonConverter(typeof(LooseStringConverter))] public string? AirDate { get; init; }
    [JsonPropertyName("releasedate")][JsonConverter(typeof(LooseStringConverter))] public string? ReleaseDate { get; init; }
    [JsonPropertyName("release_date")][JsonConverter(typeof(LooseStringConverter))] public string? ReleaseDateAlt { get; init; }
}
