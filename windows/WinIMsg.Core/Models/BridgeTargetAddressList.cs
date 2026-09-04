namespace WinIMsg.Core.Models;

public static class BridgeTargetAddressList
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? addresses)
    {
        var normalized = new List<string>();
        if (addresses is not null)
        {
            foreach (var address in addresses)
            {
                AddAddress(normalized, address);
            }
        }

        return normalized;
    }

    public static IReadOnlyList<string> ParseMultiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddAddress(List<string> addresses, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var trimmed = address.Trim();
        if (!addresses.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            addresses.Add(trimmed);
        }
    }
}
