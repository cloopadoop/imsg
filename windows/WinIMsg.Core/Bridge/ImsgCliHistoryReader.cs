using System.Globalization;
using System.Text.Json;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.Core.Ssh;

namespace WinIMsg.Core.Bridge;

public sealed class ImsgCliHistoryReader
{
    private readonly SshCommandRunner _commandRunner;

    public ImsgCliHistoryReader(SshCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new SshCommandRunner();
    }

    public async Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
        ImsgBridgeSettings settings,
        long chatId,
        int limit,
        bool includeAttachments = true,
        bool convertAttachments = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "history",
            "--chat-id",
            chatId.ToString(CultureInfo.InvariantCulture),
            "--limit",
            Math.Max(limit, 1).ToString(CultureInfo.InvariantCulture),
            "--json"
        };

        if (includeAttachments)
        {
            arguments.Add("--attachments");
        }

        if (convertAttachments)
        {
            arguments.Add("--convert-attachments");
        }

        var result = await _commandRunner.RunImsgCommandAsync(settings, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to read imsg history: {result.ErrorSummary}");
        }

        return ParseMessages(result.StandardOutput);
    }

    private static IReadOnlyList<ImsgMessage> ParseMessages(string standardOutput)
    {
        var messages = new List<ImsgMessage>();
        foreach (var line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var message = JsonSerializer.Deserialize<ImsgMessage>(line, ImsgJson.Options);
                if (message is not null)
                {
                    messages.Add(message);
                }
            }
            catch (JsonException ex)
            {
                throw new JsonException($"imsg history returned invalid JSON: {ex.Message}", ex);
            }
        }

        return messages;
    }
}
