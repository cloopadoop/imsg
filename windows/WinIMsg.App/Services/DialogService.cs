using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIMsg.App.Services;

public interface IUserDialogService
{
    Task<bool> ConfirmAsync(string title, string message);

    Task<TapbackChoice?> PromptTapbackAsync();

    Task<string?> PromptTextAsync(string title, string header, string? initialValue);

    Task<PollComposeRequest?> PromptPollAsync();

    Task<NewChatRequest?> PromptNewChatAsync();
}

public sealed class WinUiDialogService(Func<XamlRoot?> xamlRootProvider) : IUserDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Confirm");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<TapbackChoice?> PromptTapbackAsync()
    {
        var reactionBox = new ComboBox
        {
            Header = "Reaction",
            SelectedIndex = 0,
            ItemsSource = new[] { "love", "like", "dislike", "laugh", "emphasize", "question" }
        };
        var removeBox = new CheckBox { Content = "Remove reaction" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(reactionBox);
        panel.Children.Add(removeBox);

        var dialog = CreateDialog("Tapback", panel, "Send");
        var result = await dialog.ShowAsync();
        return result != ContentDialogResult.Primary || reactionBox.SelectedItem is not string reaction
            ? null
            : new TapbackChoice(reaction, removeBox.IsChecked == true);
    }

    public async Task<string?> PromptTextAsync(string title, string header, string? initialValue)
    {
        var box = new TextBox
        {
            Header = header,
            Text = initialValue ?? string.Empty,
            AcceptsReturn = true,
            MaxHeight = 160
        };

        var dialog = CreateDialog(title, box, "Apply");
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public async Task<PollComposeRequest?> PromptPollAsync()
    {
        var questionBox = new TextBox
        {
            Header = "Question",
            PlaceholderText = "Dinner?"
        };
        var optionsBox = new TextBox
        {
            Header = "Options",
            PlaceholderText = $"Pizza{Environment.NewLine}Sushi",
            AcceptsReturn = true,
            MinHeight = 120,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(questionBox);
        panel.Children.Add(optionsBox);

        var dialog = CreateDialog("New Poll", panel, "Send");
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return PollComposeRequest.Create(questionBox.Text, optionsBox.Text);
    }

    public async Task<NewChatRequest?> PromptNewChatAsync()
    {
        var recipientBox = new TextBox
        {
            Header = "To",
            PlaceholderText = "Phone number or email"
        };
        var messageBox = new TextBox
        {
            Header = "Message",
            AcceptsReturn = true,
            MinHeight = 96,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(recipientBox);
        panel.Children.Add(messageBox);

        var dialog = CreateDialog("New Message", panel, "Send");
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var recipient = recipientBox.Text.Trim();
        var text = messageBox.Text.Trim();
        return string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(text)
            ? null
            : new NewChatRequest(recipient, text);
    }

    private ContentDialog CreateDialog(string title, object content, string primaryText)
    {
        return new ContentDialog
        {
            XamlRoot = xamlRootProvider(),
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
    }
}

public readonly record struct TapbackChoice(string Reaction, bool Remove);

public sealed record NewChatRequest(string Recipient, string Text);

public sealed record PollComposeRequest(string Question, IReadOnlyList<string> Options)
{
    public static PollComposeRequest? Create(string? question, string? rawOptions)
    {
        var normalizedQuestion = question?.Trim() ?? string.Empty;
        var options = ParseOptions(rawOptions);
        return string.IsNullOrWhiteSpace(normalizedQuestion) || options.Count < 2
            ? null
            : new PollComposeRequest(normalizedQuestion, options);
    }

    public static IReadOnlyList<string> ParseOptions(string? rawOptions)
    {
        return (rawOptions ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
