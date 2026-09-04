using System.Globalization;
using System.Text.Json;

namespace WinIMsg.Core.Models;

public static class JsonElementReaders
{
    public static string? ReadString(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number => element.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static DateTimeOffset? ReadDateTimeOffset(JsonElement? element)
    {
        var value = ReadString(element);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return null;
    }

    public static bool ReadBoolean(JsonElement? element)
    {
        if (element is null)
        {
            return false;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.Value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(element.Value.GetString(), out var parsed)
                ? parsed
                : long.TryParse(element.Value.GetString(), out var number) && number != 0,
            JsonValueKind.Object => true,
            _ => false
        };
    }

    public static int? ReadInt32(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.Value.ValueKind == JsonValueKind.Number && element.Value.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.Value.ValueKind == JsonValueKind.String && int.TryParse(element.Value.GetString(), out number))
        {
            return number;
        }

        return null;
    }
}
