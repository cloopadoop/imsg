using System.Diagnostics;

namespace WinIMsg.App.Services;

internal static class StartupRegistration
{
    private const string ShortcutName = "win-imsg.lnk";

    public static void Apply(bool enabled)
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupFolder))
        {
            return;
        }

        var shortcutPath = Path.Combine(startupFolder, ShortcutName);
        if (!enabled)
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }

            return;
        }

        CreateShortcut(shortcutPath, Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.Description = "win-imsg";
        shortcut.Save();
    }
}
