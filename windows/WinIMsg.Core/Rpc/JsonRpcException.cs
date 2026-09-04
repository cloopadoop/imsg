using System.Text.Json;

namespace WinIMsg.Core.Rpc;

public class JsonRpcException : Exception
{
    public JsonRpcException(string message)
        : base(message)
    {
    }

    public JsonRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class JsonRpcRemoteException : JsonRpcException
{
    public JsonRpcRemoteException(int code, string message, JsonElement rawError)
        : base(message)
    {
        Code = code;
        RawError = rawError;
    }

    public int Code { get; }

    public JsonElement RawError { get; }

    public static JsonRpcRemoteException From(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode
            : 0;
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "imsg returned an RPC error."
            : "imsg returned an RPC error.";
        return new JsonRpcRemoteException(code, message, error);
    }
}
