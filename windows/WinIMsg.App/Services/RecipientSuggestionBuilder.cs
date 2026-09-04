using WinIMsg.App.ViewModels;

namespace WinIMsg.App.Services;

public static class RecipientSuggestionBuilder
{
    public static IReadOnlyList<string> Build(IEnumerable<ChatListItem> chats)
    {
        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chat in chats)
        {
            if (chat.IsNewMessageDraft)
            {
                continue;
            }

            Add(chat.DisplayName, suggestions, seen);
            foreach (var source in chat.Chats)
            {
                if (ContactIdentityService.HasExplicitContactName(source))
                {
                    Add(source.ContactName, suggestions, seen);
                }

                if (source.IsGroup)
                {
                    Add(source.Name, suggestions, seen);
                }

                Add(ContactIdentityService.AddressFromChatIdentifier(source.Identifier), suggestions, seen);
                foreach (var participant in source.Participants)
                {
                    Add(participant, suggestions, seen);
                }
            }
        }

        return suggestions;
    }

    private static void Add(string? value, ICollection<string> suggestions, ISet<string> seen)
    {
        var normalized = DisplayTextFormatter.SingleLine(value, string.Empty, maxLength: 160);
        if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
        {
            return;
        }

        suggestions.Add(normalized);
    }
}
