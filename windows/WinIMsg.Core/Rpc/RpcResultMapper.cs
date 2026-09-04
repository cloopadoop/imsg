using System.Text.Json;

namespace WinIMsg.Core.Rpc;

public static class RpcResultMapper
{
    public static IReadOnlyList<T> ReadArray<T>(JsonElement result, string propertyName)
    {
        var source = result;
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty(propertyName, out var nested))
        {
            source = nested;
        }

        if (source.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return source.Deserialize<IReadOnlyList<T>>(ImsgJson.Options) ?? [];
    }
}
