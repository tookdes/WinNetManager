using System;
using System.Diagnostics;
using System.IO;

namespace WinNetManager.Services;

public static class RegistryBackupService
{
    /// <summary>
    /// 将注册表键导出到 .reg 文件。失败时抛出异常（包含 reg.exe 的错误信息）。
    /// </summary>
    public static string BackupKeyToPath(string registryPath, string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"export \"{registryPath}\" \"{filePath}\" /y",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"无法启动 reg.exe 导出 {registryPath}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"导出 {registryPath} 超时。");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"导出失败（退出码 {proc.ExitCode}）：{registryPath}\n{detail.Trim()}");
        }

        return filePath;
    }

    /// <summary>
    /// 将任意命令（netsh 等）的输出保存到文件。失败时抛出异常。
    /// </summary>
    public static string BackupCommandToPath(string fileName, string arguments, string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"无法启动 {fileName} 执行备份。");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"{fileName} 备份超时。");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"命令失败（退出码 {proc.ExitCode}）：{fileName} {arguments}\n{detail.Trim()}");
        }

        File.WriteAllText(filePath, stdout);
        return filePath;
    }
}
