using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIMsg.Core.Models;

public sealed record ImsgCapabilities
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("bridge_version")]
    public JsonElement? BridgeVersionRaw { get; init; }

    [JsonPropertyName("v2_ready")]
    public bool V2Ready { get; init; }

    [JsonPropertyName("typing_indicators")]
    public bool TypingIndicators { get; init; }

    [JsonPropertyName("read_receipts")]
    public bool ReadReceipts { get; init; }

    [JsonPropertyName("rpc_methods")]
    public IReadOnlyList<string> RpcMethods { get; init; } = [];

    [JsonPropertyName("basic_features")]
    public JsonElement? BasicFeatures { get; init; }

    [JsonPropertyName("advanced_features")]
    public JsonElement? AdvancedFeaturesRaw { get; init; }

    [JsonPropertyName("sip")]
    public JsonElement? Sip { get; init; }

    [JsonPropertyName("selectors")]
    public JsonElement? Selectors { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }

    public bool Supports(string method) => RpcMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    public bool SupportsAny(params string[] methods) => methods.Any(Supports);

    [JsonIgnore]
    public bool BasicFeaturesAdvertised => BasicFeatures is not null;

    [JsonIgnore]
    public bool BasicFeaturesEnabled => JsonElementReaders.ReadBoolean(BasicFeatures);

    [JsonIgnore]
    public string? BridgeVersion => JsonElementReaders.ReadString(BridgeVersionRaw);

    [JsonIgnore]
    public bool AdvancedFeaturesEnabled => JsonElementReaders.ReadBoolean(AdvancedFeaturesRaw);

    [JsonIgnore]
    public bool HasAdvancedBridge
    {
        get
        {
            if (V2Ready)
            {
                return true;
            }

            // bridge_version reports the live injection state, while
            // advanced_features only says the Mac is capable (SIP off, helper
            // dylib present). A reported bridge_version of 0 means Messages is
            // running without the helper, so bridge-backed actions like
            // tapback/read/typing would hang; they must stay disabled until
            // imsg launch re-injects.
            var bridgeVersionNumber = JsonElementReaders.ReadInt32(BridgeVersionRaw);
            if (bridgeVersionNumber is not null)
            {
                return bridgeVersionNumber > 0;
            }

            var bridgeVersion = BridgeVersion;
            if (!string.IsNullOrWhiteSpace(bridgeVersion))
            {
                return !string.Equals(bridgeVersion, "0", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(bridgeVersion, "false", StringComparison.OrdinalIgnoreCase);
            }

            // Older status contracts without bridge_version/v2_ready only
            // expose advanced_features; trust it there.
            return AdvancedFeaturesEnabled;
        }
    }

    public bool SupportsAdvanced(string method) => HasAdvancedBridge && Supports(method);

    public bool SupportsSelector(params string[] selectorNames)
    {
        if (Selectors is not { ValueKind: JsonValueKind.Object } selectors)
        {
            return true;
        }

        var sawSelector = false;
        foreach (var selectorName in selectorNames)
        {
            if (selectors.TryGetProperty(selectorName, out var selector))
            {
                sawSelector = true;
                if (JsonElementReaders.ReadBoolean(selector))
                {
                    return true;
                }
            }
        }

        return !sawSelector;
    }
}
