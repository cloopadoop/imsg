namespace WinIMsg.App.Services;

public sealed class AppLogService
{
    private readonly object _sync = new();

    public AppLogService(string logDirectory)
    {
        LogDirectory = logDirectory;
        Directory.CreateDirectory(LogDirectory);
        LogFilePath = Path.Combine(LogDirectory, "win-imsg.log");
    }

    public string LogDirectory { get; }

    public string LogFilePath { get; }

    public void EnsureLogFile()
    {
        Directory.CreateDirectory(LogDirectory);
        if (!File.Exists(LogFilePath))
        {
            File.WriteAllText(LogFilePath, string.Empty);
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                File.AppendAllText(LogFilePath, entry);
            }
        }
        catch
        {
            // Logging must never break the chat UI.
        }
    }
}
