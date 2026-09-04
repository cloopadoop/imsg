using System.Globalization;
using System.Collections.Concurrent;
using PhoneNumbers;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public static class ParticipantMergeKeyBuilder
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();
    private static readonly ConcurrentDictionary<string, string> NormalizedParticipantCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Build(ImsgChat chat, string? phoneNumberRegion)
    {
        var participants = chat.Participants.Count > 0
            ? chat.Participants
            : ExtractIdentifierParticipants(chat.Identifier);
        var normalized = participants
            .Select(participant => NormalizeParticipant(participant, phoneNumberRegion))
            .Where(static participant => !string.IsNullOrWhiteSpace(participant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : string.Join("|", normalized);
    }

    public static string NormalizeRegionSetting(string? region)
    {
        var trimmed = region?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return "AUTO";
        }

        return trimmed.Length == 2 && trimmed.All(char.IsLetter)
            ? trimmed.ToUpperInvariant()
            : "AUTO";
    }

    public static string ResolveEffectiveRegion(string? region)
    {
        var normalized = NormalizeRegionSetting(region);
        if (!string.Equals(normalized, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(CultureInfo.CurrentCulture.Name))
            {
                var currentRegion = new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
                if (currentRegion.Length == 2)
                {
                    return currentRegion.ToUpperInvariant();
                }
            }
        }
        catch (ArgumentException)
        {
            // Fall through to CurrentRegion below.
        }

        try
        {
            var currentRegion = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return currentRegion.Length == 2 ? currentRegion.ToUpperInvariant() : string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> ExtractIdentifierParticipants(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return [];
        }

        var value = identifier.Trim();
        var separatorIndex = value.LastIndexOf(";-;", StringComparison.Ordinal);
        if (separatorIndex >= 0 && separatorIndex + 3 < value.Length)
        {
            value = value[(separatorIndex + 3)..];
        }

        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeParticipant(string? participant, string? phoneNumberRegion)
    {
        if (string.IsNullOrWhiteSpace(participant))
        {
            return string.Empty;
        }

        var trimmed = participant.Trim().ToLowerInvariant();
        var cacheKey = $"{NormalizeRegionSetting(phoneNumberRegion)}\u001f{trimmed}";
        return NormalizedParticipantCache.GetOrAdd(cacheKey, _ => NormalizeParticipantUncached(trimmed, phoneNumberRegion));
    }

    private static string NormalizeParticipantUncached(string trimmed, string? phoneNumberRegion)
    {
        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (ContainsAsciiLetter(trimmed))
        {
            return trimmed;
        }

        var phoneKey = TryNormalizePhoneNumber(trimmed, phoneNumberRegion);
        if (!string.IsNullOrWhiteSpace(phoneKey))
        {
            return phoneKey;
        }

        return trimmed;
    }

    private static string? TryNormalizePhoneNumber(string value, string? phoneNumberRegion)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        var hasExplicitCountryPrefix = value.TrimStart().StartsWith('+');
        if (hasExplicitCountryPrefix && IsPlainPhoneToken(value))
        {
            return "tel:+" + digits;
        }

        if (digits.Length < 7 && !value.TrimStart().StartsWith('+'))
        {
            return $"tel-local:short:{digits}";
        }

        var region = hasExplicitCountryPrefix ? null : ResolveEffectiveRegion(phoneNumberRegion);
        if (hasExplicitCountryPrefix || !string.IsNullOrWhiteSpace(region))
        {
            try
            {
                var parsed = PhoneUtil.Parse(value, region);
                if (PhoneUtil.IsValidNumber(parsed))
                {
                    return "tel:" + PhoneUtil.Format(parsed, PhoneNumberFormat.E164);
                }
            }
            catch (NumberParseException)
            {
                // Use the conservative fallback below for malformed or ambiguous values.
            }
        }

        if (hasExplicitCountryPrefix)
        {
            return "tel:+" + digits;
        }

        var effectiveRegion = string.IsNullOrWhiteSpace(region) ? "local" : region.ToUpperInvariant();
        return $"tel-local:{effectiveRegion}:{digits}";
    }

    private static bool ContainsAsciiLetter(string value) =>
        value.Any(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    private static bool IsPlainPhoneToken(string value)
    {
        var sawPlus = false;
        foreach (var character in value)
        {
            if (char.IsDigit(character) || char.IsWhiteSpace(character))
            {
                continue;
            }

            if (character == '+')
            {
                if (sawPlus)
                {
                    return false;
                }

                sawPlus = true;
                continue;
            }

            if (character is '-' or '(' or ')' or '.')
            {
                continue;
            }

            return false;
        }

        return sawPlus;
    }
}
