using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxDo.Core.Utilities;

/// <summary>Helpers for Discourse's occasionally flexible JSON shapes.</summary>
public static class JsonFlexible
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new FlexibleBoolConverter(),
            new FlexibleIntConverter(),
            new FlexibleStringConverter()
        }
    };

    public static int? GetInt(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var i) => i,
            JsonValueKind.Number when el.TryGetDouble(out var d) => (int)d,
            JsonValueKind.String when int.TryParse(el.GetString(), out var s) => s,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    public static double? GetDouble(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), out var s) => s,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    public static bool? GetBool(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.GetDouble() != 0,
            JsonValueKind.String => el.GetString()?.ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => null
        };
    }

    public static string? GetString(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static List<string>? GetStringArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    list.Add(n.GetString() ?? "");
                else if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    list.Add(id.GetString() ?? "");
            }
        }
        return list;
    }

    public static List<int>? GetIntArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            var v = GetInt(item);
            if (v.HasValue) list.Add(v.Value);
        }
        return list;
    }

    public static T? Prop<T>(JsonElement obj, string name, Func<JsonElement, T?> reader)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return default;
        return reader(el);
    }

    public static List<T> DecodeLossyArray<T>(JsonElement el, Func<JsonElement, T?> factory)
    {
        var list = new List<T>();
        if (el.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in el.EnumerateArray())
        {
            try
            {
                var v = factory(item);
                if (v is not null) list.Add(v);
            }
            catch
            {
                // skip bad rows
            }
        }
        return list;
    }
}

public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.TryGetInt32(out var i) ? i != 0 : reader.GetDouble() != 0,
            JsonTokenType.String => reader.GetString()?.ToLowerInvariant() is "1" or "true" or "yes" or "on",
            JsonTokenType.Null => false,
            _ => false
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
            JsonTokenType.Number => (int)reader.GetDouble(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var s) => s,
            _ => 0
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
