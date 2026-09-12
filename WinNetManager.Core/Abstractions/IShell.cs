
namespace WinNetManager.Core.Abstractions;

/// <summary>
/// 进程执行抽象。生产实现包装主项目的 ProcessRunner，测试用 FakeShell。
/// </summary>
public interface IShell
{
    /// <summary>执行外部进程，返回 stdout，同时输出 stderr 与退出码。</summary>
    string Run(string fileName, IEnumerable<string> arguments, out string error, out int exitCode, int timeoutMs);

    /// <summary>执行 PowerShell 脚本，返回 stdout，同时输出 stderr 与退出码。</summary>
    string RunPowerShell(string script, out string error, out int exitCode, int timeoutMs);

    string RunPowerShell(string script, out string error, int timeoutMs);
}
