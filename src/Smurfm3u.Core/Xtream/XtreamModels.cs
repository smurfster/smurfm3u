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
/// The episode map is keyed by season number, except on the panels that send an empty array
/// instead of an empty object when a series has no episodes. That array is the shape this is
/// really here for; deserialising it as a dictionary is what would otherwise throw.
/// </summary>
public sealed class EpisodeMapConverter : JsonConverter<Dictionary<string, List<XtreamEpisode>>>
{
    public override Dictionary<string, List<XtreamEpisode>> Read(
        ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.Null)
        {
            reader.Skip();
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, List<XtreamEpisode>>>(ref reader, options) ?? [];
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, List<XtreamEpisode>> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>What the panel says about the account, used to fail a test with a real reason.</summary>
public sealed class XtreamAuth
{
    [JsonPropertyName("user_info")] public XtreamUserInfo? UserInfo { get; init; }
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
    [JsonPropertyName("info")] public XtreamEpisodeInfo? Info { get; init; }
}

public sealed class XtreamEpisodeInfo
{
    [JsonPropertyName("duration_secs")][JsonConverter(typeof(LooseIntConverter))] public int? DurationSeconds { get; init; }
}
