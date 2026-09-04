using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIMsg.Core.Rpc;

public static class ImsgJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
