namespace SVNRevDesk.Core.Svn;

public sealed class SvnException(string command, int exitCode, string error)
    : Exception($"SVN {command} 执行失败（{exitCode}）。\n{error.Trim()}")
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string ErrorOutput { get; } = error;
    public bool IsAuthenticationError => ErrorOutput.Contains("E170001", StringComparison.Ordinal)
        || ErrorOutput.Contains("E215004", StringComparison.Ordinal)
        || ErrorOutput.Contains("Authentication", StringComparison.OrdinalIgnoreCase);
}
