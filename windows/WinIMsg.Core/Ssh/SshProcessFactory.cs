using System.Diagnostics;
using System.Text;
using WinIMsg.Core.Models;

namespace WinIMsg.Core.Ssh;

public static class SshProcessFactory
{
    private const string SshExecutableName = "ssh.exe";
    private const string ScpExecutableName = "scp.exe";
    private const string SftpExecutableName = "sftp.exe";

    public static ProcessStartInfo Create(ImsgBridgeSettings settings, IReadOnlyList<string> remoteArguments)
    {
        var normalized = settings.Normalize();
        return CreateRemoteCommand(normalized, [normalized.ImsgPath, .. remoteArguments]);
    }

    public static ProcessStartInfo CreateRemoteCommand(ImsgBridgeSettings settings, IReadOnlyList<string> remoteArguments)
    {
        var normalized = settings.Normalize();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveOpenSshExecutable(SshExecutableName),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(normalized.SshPort.ToString());
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ServerAliveInterval=30");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ServerAliveCountMax=2");

        if (!string.IsNullOrWhiteSpace(normalized.IdentityFile))
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(normalized.IdentityFile);
        }

        startInfo.ArgumentList.Add(normalized.SshTarget);
        foreach (var argument in remoteArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static string ResolveScpExecutable() => ResolveOpenSshExecutable(ScpExecutableName);

    public static string ResolveSftpExecutable() => ResolveOpenSshExecutable(SftpExecutableName);

    public static string ResolveOpenSshExecutable(string executableName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFileNameWithoutExtension(executableName);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            windowsDirectory = Environment.GetEnvironmentVariable("WINDIR") ?? "C:\\Windows";
        }

        var candidates = new List<string>();
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            candidates.Add(Path.Combine(windowsDirectory, "Sysnative", "OpenSSH", executableName));
        }

        candidates.Add(Path.Combine(windowsDirectory, "System32", "OpenSSH", executableName));
        candidates.Add(Path.Combine(windowsDirectory, "SysWOW64", "OpenSSH", executableName));

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, executableName)));
        }

        return candidates.FirstOrDefault(File.Exists) ?? executableName;
    }
}
