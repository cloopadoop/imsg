namespace WinIMsg.Core;

public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIMsg")
            : root;

        Logs = Path.Combine(Root, "logs");
        Attachments = Path.Combine(Root, "attachments");
        SupportBundles = Path.Combine(Root, "support-bundles");
        VoiceMessages = Path.Combine(Root, "voice-messages");
        DatabasePath = Path.Combine(Root, "cache.db");
        SettingsPath = Path.Combine(Root, "settings.json");
    }

    public string Root { get; }

    public string Logs { get; }

    public string Attachments { get; }

    public string SupportBundles { get; }

    public string VoiceMessages { get; }

    public string DatabasePath { get; }

    public string SettingsPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Attachments);
        Directory.CreateDirectory(SupportBundles);
        Directory.CreateDirectory(VoiceMessages);
    }
}
