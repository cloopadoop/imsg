using System.Text;
using System.Text.RegularExpressions;

namespace WinIMsg.App.Services;

public static class DisplayTextFormatter
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex MessagesAttachmentPathRegex = new(
        @"(?im)(?:^|[,\s]+)(?:(?:~|/Users/[^/\s,]+|/private/var/[^/\s,]+)?/Library/Messages/Attachments/[^,\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string MessageText(string? value, string fallback = " ")
    {
        return Normalize(value, fallback, singleLine: false, maxLength: 4000, removeMessagesAttachmentPaths: true);
    }

    public static string SingleLine(string? value, string fallback, int maxLength = 260)
    {
        return Normalize(value, fallback, singleLine: true, maxLength, removeMessagesAttachmentPaths: false);
    }

    private static string Normalize(string? value, string fallback, bool singleLine, int maxLength, bool removeMessagesAttachmentPaths)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = RepairUtf8Mojibake(value.Normalize(NormalizationForm.FormC));
        text = ReplaceKnownMojibake(text);

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t')
            {
                builder.Append(singleLine ? ' ' : character);
                continue;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        var normalized = removeMessagesAttachmentPaths
            ? RemoveMessagesAttachmentPaths(builder.ToString())
            : builder.ToString();

        var cleaned = singleLine
            ? CollapseWhitespace(normalized)
            : TrimMultiline(normalized);

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        return cleaned.Length <= maxLength
            ? cleaned
            : string.Concat(cleaned.AsSpan(0, Math.Max(0, maxLength - 3)), "...");
    }

    private static string RemoveMessagesAttachmentPaths(string text)
    {
        return MessagesAttachmentPathRegex.Replace(text, " ");
    }

    private static string TrimMultiline(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string RepairUtf8Mojibake(string text)
    {
        if (!LooksLikeMojibake(text))
        {
            return text;
        }

        var bytes = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            if (!TryMapWindows1252Byte(text[index], out bytes[index]))
            {
                return text;
            }
        }

        try
        {
            var repaired = StrictUtf8.GetString(bytes).Normalize(NormalizationForm.FormC);
            return MojibakeScore(repaired) < MojibakeScore(text) ? repaired : text;
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    private static string ReplaceKnownMojibake(string text)
    {
        return text
            .Replace("\u00C2\u00A0", " ")
            .Replace("\u00E2\u20AC\u2122", "'")
            .Replace("\u00E2\u20AC\u02DC", "'")
            .Replace("\u00E2\u20AC\u0153", "\"")
            .Replace("\u00E2\u20AC\u009D", "\"")
            .Replace("\u00E2\u20AC\u201D", "-")
            .Replace("\u00E2\u20AC\u201C", "-")
            .Replace("\u00E2\u20AC\u00A6", "...")
            .Replace("\u00E2\u20AC\u00A2", "*")
            .Replace("\u00E2\u20AC\u0160", " ")
            .Replace("\uFFFC", string.Empty)
            .Replace("\u00FFFD", string.Empty);
    }

    private static bool LooksLikeMojibake(string text)
    {
        return text.Contains('\u00C2') ||
            text.Contains('\u00C3') ||
            text.Contains('\u00E2') ||
            text.Contains('\u00EF') ||
            text.Contains('\u00F0') ||
            text.Contains('\uFFFD');
    }

    private static int MojibakeScore(string text)
    {
        var score = 0;
        foreach (var character in text)
        {
            score += character switch
            {
                '\uFFFD' => 10,
                '\u00C2' or '\u00C3' or '\u00E2' or '\u00EF' or '\u00F0' => 4,
                '\u20AC' or '\u2122' or '\u0153' or '\u009D' => 3,
                _ => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' ? 2 : 0
            };
        }

        return score;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool TryMapWindows1252Byte(char character, out byte value)
    {
        if (character <= '\u00FF')
        {
            value = (byte)character;
            return true;
        }

        value = character switch
        {
            '\u20AC' => 0x80,
            '\u201A' => 0x82,
            '\u0192' => 0x83,
            '\u201E' => 0x84,
            '\u2026' => 0x85,
            '\u2020' => 0x86,
            '\u2021' => 0x87,
            '\u02C6' => 0x88,
            '\u2030' => 0x89,
            '\u0160' => 0x8A,
            '\u2039' => 0x8B,
            '\u0152' => 0x8C,
            '\u017D' => 0x8E,
            '\u2018' => 0x91,
            '\u2019' => 0x92,
            '\u201C' => 0x93,
            '\u201D' => 0x94,
            '\u2022' => 0x95,
            '\u2013' => 0x96,
            '\u2014' => 0x97,
            '\u02DC' => 0x98,
            '\u2122' => 0x99,
            '\u0161' => 0x9A,
            '\u203A' => 0x9B,
            '\u0153' => 0x9C,
            '\u017E' => 0x9E,
            '\u0178' => 0x9F,
            _ => 0
        };

        return value != 0;
    }
}
