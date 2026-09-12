
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Services.Automation;

/// <summary>IShell 生产实现：包装现有 ProcessRunner。</summary>
public sealed class ShellAdapter : IShell
{
    public string Run(string fileName, IEnumerable<string> arguments, out string error, out int exitCode, int timeoutMs)
        => ProcessRunner.Run(fileName, arguments, out error, out exitCode, timeoutMs);

    public string RunPowerShell(string script, out string error, out int exitCode, int timeoutMs)
        => ProcessRunner.RunPowerShell(script, out error, out exitCode, timeoutMs);

    public string RunPowerShell(string script, out string error, int timeoutMs)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);
}
