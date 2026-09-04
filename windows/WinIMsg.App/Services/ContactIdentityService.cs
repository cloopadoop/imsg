using System.Globalization;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;

namespace WinIMsg.App.Services;

public static class ContactIdentityService
{
    public static List<ImsgChat> MergeExplicitContactNamesByChatIdentity(
        IEnumerable<ImsgChat> chats,
        IEnumerable<ImsgChat> contactSourceChats)
    {
        var contactNamesByKey = new Dictionary<string, ContactNameCandidate>(StringComparer.OrdinalIgnoreCase);
        var contactNamesByParticipant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in contactSourceChats)
        {
            if (!HasExplicitContactName(source))
            {
                continue;
            }

            if (!source.IsGroup)
            {
                foreach (var participant in ParticipantIdentityKeys(source))
                {
                    contactNamesByParticipant.TryAdd(participant, source.ContactName!.Trim());
                }
            }

            foreach (var key in ExactChatIdentityKeys(source))
            {
                contactNamesByKey.TryAdd(key, new ContactNameCandidate(source.ContactName!.Trim(), source.IsGroup));
            }
        }

        return chats.Select(chat =>
        {
            if (HasExplicitContactName(chat))
            {
                return chat;
            }

            foreach (var key in ExactChatIdentityKeys(chat))
            {
                if (contactNamesByKey.TryGetValue(key, out var contactName) &&
                    contactName.IsGroup == chat.IsGroup)
                {
                    return chat with { ContactName = contactName.Value };
                }
            }

            if (chat.IsGroup &&
                string.IsNullOrWhiteSpace(chat.ContactName) &&
                !HasTrustedGroupName(chat) &&
                TryBuildGroupParticipantDisplayName(chat, contactNamesByParticipant, out var groupDisplayName))
            {
                return chat with { Name = groupDisplayName };
            }

            return chat;
        }).ToList();
    }

    public static string BuildContactNamesDiagnostic(
        IReadOnlyList<ImsgChat> chats,
        IReadOnlyList<ImsgChat>? imsgChatsJson = null,
        MacContactsAuthorizationResult? authorization = null,
        string? requestNote = null)
    {
        if (chats.Count == 0)
        {
            return "No chats are available to inspect yet. Connect to the Mac and refresh chats first.";
        }

        var contactNameCount = chats.Count(HasExplicitContactName);
        var resolvedNameCount = chats.Count(HasResolvedDisplayName);
        var rawHandleCount = chats.Count(chat => LooksLikeRawHandle(chat.DisplayName));
        var imsgChatsJsonCount = imsgChatsJson?.Count ?? 0;
        var imsgChatsJsonContactNameCount = imsgChatsJson?.Count(HasExplicitContactName) ?? 0;
        var oneToOneCount = chats.Count(static chat => !chat.IsGroup);
        var oneToOneContactNameCount = chats.Count(static chat => !chat.IsGroup && HasExplicitContactName(chat));
        var oneToOneRawHandleCount = chats.Count(static chat => !chat.IsGroup && LooksLikeRawHandle(chat.DisplayName));
        var groupCount = chats.Count(static chat => chat.IsGroup);
        var resolvedGroupCount = chats.Count(static chat => chat.IsGroup && HasResolvedDisplayName(chat));
        var imsgOneToOneCount = imsgChatsJson?.Count(static chat => !chat.IsGroup) ?? 0;
        var imsgOneToOneContactNameCount = imsgChatsJson?.Count(static chat => !chat.IsGroup && HasExplicitContactName(chat)) ?? 0;
        var resolvedExamples = string.Join(", ", chats
            .Where(HasResolvedDisplayName)
            .Select(static chat => DisplayTextFormatter.SingleLine(chat.ContactName ?? chat.Name ?? chat.DisplayName, string.Empty, maxLength: 48))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3));
        var imsgContactNameExamples = string.Join(", ", (imsgChatsJson ?? [])
            .Where(HasExplicitContactName)
            .Select(static chat => DisplayTextFormatter.SingleLine(chat.ContactName, string.Empty, maxLength: 48))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3));
        var rawExamples = string.Join(", ", chats
            .Where(static chat => LooksLikeRawHandle(chat.DisplayName))
            .Select(static chat => DisplayTextFormatter.SingleLine(chat.DisplayName, string.Empty, maxLength: 48))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3));
        var exampleSuffix = string.IsNullOrWhiteSpace(resolvedExamples)
            ? string.Empty
            : $" Resolved examples: {resolvedExamples}.";
        var authorizationSuffix = authorization is null
            ? string.Empty
            : $" CNContactStore authorization probe returned {authorization.State}" +
                (string.IsNullOrWhiteSpace(authorization.RawValue) ? string.Empty : $" (raw={authorization.RawValue})") +
                ".";
        var imsgChatsJsonSuffix = imsgChatsJsonCount == 0
            ? string.Empty
            : $" imsg chats --json returned contact_name on {imsgChatsJsonContactNameCount}/{imsgChatsJsonCount} inspected chats before cache/RPC merge." +
                $" CLI one-to-one contact_name: {imsgOneToOneContactNameCount}/{imsgOneToOneCount}." +
                (string.IsNullOrWhiteSpace(imsgContactNameExamples) ? string.Empty : $" CLI contact_name examples: {imsgContactNameExamples}.");
        var requestSuffix = string.IsNullOrWhiteSpace(requestNote)
            ? string.Empty
            : $" {requestNote}";
        var sourceBreakdownSuffix = $" One-to-one exact contact_name: {oneToOneContactNameCount}/{oneToOneCount}; one-to-one raw handles: {oneToOneRawHandleCount}/{oneToOneCount}; group display names/participants: {resolvedGroupCount}/{groupCount}. Group names are never used as one-to-one contact names.";

        if (rawHandleCount == 0)
        {
            return $"imsg says contact_name is present on {contactNameCount}/{chats.Count} chats and resolved display names are present on {resolvedNameCount}/{chats.Count}.{sourceBreakdownSuffix}{exampleSuffix}{imsgChatsJsonSuffix}{authorizationSuffix}{requestSuffix}";
        }

        var rawSuffix = string.IsNullOrWhiteSpace(rawExamples)
            ? string.Empty
            : $" Raw examples: {rawExamples}.";
        var actionSuffix = authorization switch
        {
            { IsAuthorized: true } =>
                " imsg is the source of truth here; remaining raw handles probably do not match local Contacts records or need phone-region normalization.",
            { IsNotDetermined: true } =>
                " Contacts is still not determined for the probe process. win-imsg tried imsg's local Address Book path; if macOS suppressed the prompt, run Prompt Mac permissions and inspect the TCC audit for sshd-keygen-wrapper/sshd-session attribution. Managed Macs can deploy a PPPC profile for kTCCServiceAddressBook to /usr/libexec/sshd-keygen-wrapper; personal Macs can review and run the generated TCC repair script, which backs up TCC.db before inserting the Contacts grant.",
            { IsDeniedOrRestricted: true } =>
                " Contacts is denied or restricted for the probe process. Approve the matching Remote Login/OpenSSH/sshd/sshd-keygen-wrapper entry in macOS Contacts privacy if it appears. If it does not appear because macOS denies prompting for the SSH platform binary, use a managed PPPC profile or the generated personal-Mac TCC repair script.",
            null =>
                " Big Mac Bridge has no Contacts-specific permission helper to reuse; it only requests Screen Recording and Accessibility.",
            _ =>
                " Contacts state could not be confirmed from the Mac; check macOS Privacy & Security > Contacts for the Remote Login/OpenSSH/sshd entry."
        };
        return $"imsg says contact_name is present on {contactNameCount}/{chats.Count} chats and resolved display names are present on {resolvedNameCount}/{chats.Count}. {rawHandleCount} chats still look like phone numbers or email handles.{sourceBreakdownSuffix}{exampleSuffix}{rawSuffix}{imsgChatsJsonSuffix}{authorizationSuffix}{requestSuffix}{actionSuffix}";
    }

    public static bool HasExplicitContactName(ImsgChat chat)
    {
        return !string.IsNullOrWhiteSpace(chat.ContactName) && !LooksLikeRawHandle(chat.ContactName);
    }

    public static bool HasResolvedDisplayName(ImsgChat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.ContactName))
        {
            return true;
        }

        if (HasTrustedGroupName(chat))
        {
            return true;
        }

        return chat.IsGroup && chat.Participants.Count > 1;
    }

    public static bool LooksLikeRawHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('@') && !trimmed.Contains(' '))
        {
            return true;
        }

        var digitCount = trimmed.Count(char.IsDigit);
        var nonHandleCharacters = trimmed.Count(character =>
            !char.IsDigit(character) &&
            character is not '+' and not '-' and not '(' and not ')' and not ' ' and not '.');

        return digitCount >= 7 && nonHandleCharacters == 0;
    }

    public static string? AddressFromChatIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var parts = identifier.Split(';');
        return parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[^1])
            ? parts[^1].Trim()
            : null;
    }

    private static IEnumerable<string> ExactChatIdentityKeys(ImsgChat chat)
    {
        if (chat.Id is not null)
        {
            yield return "id:" + chat.Id.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(chat.Guid))
        {
            yield return "guid:" + chat.Guid.Trim();
        }

        if (!string.IsNullOrWhiteSpace(chat.Identifier))
        {
            yield return "identifier:" + chat.Identifier.Trim();
        }
    }

    private static bool HasTrustedGroupName(ImsgChat chat)
    {
        return chat.IsGroup &&
            !string.IsNullOrWhiteSpace(chat.Name) &&
            !LooksLikeRawHandleList(chat.Name);
    }

    private static bool TryBuildGroupParticipantDisplayName(
        ImsgChat chat,
        IReadOnlyDictionary<string, string> contactNamesByParticipant,
        out string displayName)
    {
        displayName = string.Empty;
        var participants = chat.Participants
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (participants.Count < 2)
        {
            return false;
        }

        var labels = participants
            .Select(participant => ResolveParticipantDisplayName(participant, contactNamesByParticipant))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (labels.Count == 0 || labels.All(LooksLikeRawHandle))
        {
            return false;
        }

        displayName = string.Join(", ", labels);
        return true;
    }

    private static string ResolveParticipantDisplayName(
        string participant,
        IReadOnlyDictionary<string, string> contactNamesByParticipant)
    {
        foreach (var key in ParticipantIdentityKeys(participant))
        {
            if (contactNamesByParticipant.TryGetValue(key, out var contactName))
            {
                return contactName;
            }
        }

        return participant;
    }

    private static IEnumerable<string> ParticipantIdentityKeys(ImsgChat chat)
    {
        foreach (var participant in chat.Participants)
        {
            foreach (var key in ParticipantIdentityKeys(participant))
            {
                yield return key;
            }
        }

        var address = AddressFromChatIdentifier(chat.Identifier);
        foreach (var key in ParticipantIdentityKeys(address))
        {
            yield return key;
        }
    }

    private static IEnumerable<string> ParticipantIdentityKeys(string? participant)
    {
        if (string.IsNullOrWhiteSpace(participant))
        {
            yield break;
        }

        var trimmed = participant.Trim();
        yield return "raw:" + trimmed;

        if (trimmed.Contains('@') && !trimmed.Contains(' '))
        {
            yield return "email:" + trimmed.ToLowerInvariant();
        }

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length > 0)
        {
            yield return trimmed.TrimStart().StartsWith('+')
                ? "tel:+" + digits
                : "tel-digits:" + digits;
        }
    }

    private static bool LooksLikeRawHandleList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            return parts.All(LooksLikeRawHandle);
        }

        return LooksLikeRawHandle(value);
    }

    private sealed record ContactNameCandidate(string Value, bool IsGroup);
}
