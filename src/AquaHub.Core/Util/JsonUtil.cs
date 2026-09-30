using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AquaHub.Core.Util;

public static class JsonUtil
{
    /// <summary>Options for persisted documents (settings, snapshots).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compact options for cache blobs.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }

    // ---- JsonElement helpers (lenient accessors for third-party APIs) ----

    public static bool TryProp(this JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)
               && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    public static string? Str(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static double? Dbl(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static long? Lng(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static bool? Bool(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
    {
        if (e.TryProp(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) yield return item;
    }

    /// <summary>Returns the numeric array (nulls become NaN).</summary>
    public static double[] Doubles(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
        var list = new double[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
            list[i++] = item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d) ? d : double.NaN;
        return list;
    }

    public static long[] Longs(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<long>();
        var list = new long[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
            list[i++] = item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var l) ? l : 0;
        return list;
    }
}
