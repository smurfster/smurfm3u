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
/// </summary>
public sealed class EpisodeMapConverter : LooseObjectConverter<Dictionary<string, List<XtreamEpisode>>>
{
    protected override Dictionary<string, List<XtreamEpisode>> Absent => [];
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
}
