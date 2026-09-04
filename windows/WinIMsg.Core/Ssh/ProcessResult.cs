namespace WinIMsg.Core.Ssh;

public sealed record ProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public string ErrorSummary
    {
        get
        {
            if (TimedOut)
            {
                return "timed out";
            }

            if (!string.IsNullOrWhiteSpace(StandardError))
            {
                return StandardError.Trim();
            }

            return ExitCode is null ? "process did not exit" : $"exit code {ExitCode}";
        }
    }
}
