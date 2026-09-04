using System.Globalization;
using System.Text.Json;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.Core.Ssh;

namespace WinIMsg.Core.Bridge;

public sealed class ImsgCliChatReader
{
    private readonly SshCommandRunner _commandRunner;

    public ImsgCliChatReader(SshCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new SshCommandRunner();
    }

    public async Task<IReadOnlyList<ImsgChat>> ListChatsAsync(
        ImsgBridgeSettings settings,
        int limit = 10000,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "chats",
            "--limit",
            Math.Max(limit, 1).ToString(CultureInfo.InvariantCulture),
            "--json"
        };

        var result = await _commandRunner.RunImsgCommandAsync(settings, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to read imsg chats: {result.ErrorSummary}");
        }

        return ParseChats(result.StandardOutput);
    }

    internal static IReadOnlyList<ImsgChat> ParseChats(string standardOutput)
    {
        var chats = new List<ImsgChat>();
        foreach (var line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var chat = JsonSerializer.Deserialize<ImsgChat>(line, ImsgJson.Options);
                if (chat is not null)
                {
                    chats.Add(chat);
                }
            }
            catch (JsonException ex)
            {
                throw new JsonException($"imsg chats returned invalid JSON: {ex.Message}", ex);
            }
        }

        return chats;
    }
}
