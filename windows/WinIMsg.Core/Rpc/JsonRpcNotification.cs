using System.Text.Json;
using WinIMsg.Core.Models;

namespace WinIMsg.Core.Rpc;

public sealed record JsonRpcNotification(string Method, JsonElement Params)
{
    public bool TryGetMessage(out ImsgMessage message)
    {
        message = default!;
        if (Params.ValueKind != JsonValueKind.Object ||
            !Params.TryGetProperty("message", out var messageElement))
        {
            return false;
        }

        var parsed = messageElement.Deserialize<ImsgMessage>(ImsgJson.Options);
        if (parsed is null)
        {
            return false;
        }

        message = parsed;
        return true;
    }
}
