using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WinNetManager.Services;

public static class ProcessRunner
{
    /// <summary>
    /// Runs an external process, reading stdout/stderr asynchronously to avoid deadlocks.
    /// Kills the process if it exceeds the timeout.
    /// </summary>
    public static string Run(string fileName, string arguments, out string error, int timeoutMs = 30000)
    {
        return Run(fileName, arguments, out error, out _, timeoutMs);
    }

    public static string Run(string fileName, IEnumerable<string> arguments, out string error, int timeoutMs = 30000)
    {
        return Run(fileName, arguments, out error, out _, timeoutMs);
    }

    /// <summary>
    /// Runs an external process, reading stdout/stderr asynchronously to avoid deadlocks.
    /// Kills the process if it exceeds the timeout. Returns the process exit code.
    /// </summary>
    public static string Run(string fileName, string arguments, out string error, out int exitCode, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Run(psi, out error, out exitCode, timeoutMs);
    }

    public static string Run(string fileName, IEnumerable<string> arguments, out string error, out int exitCode, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        return Run(psi, out error, out exitCode, timeoutMs);
    }

    private static string Run(ProcessStartInfo psi, out string error, out int exitCode, int timeoutMs)
    {
        error = "";
        exitCode = -1;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "";

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            p.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                exitCode = -2;
                error = $"Process timed out after {timeoutMs} ms.";
                string timeoutError = errorBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(timeoutError))
                    error += Environment.NewLine + timeoutError.Trim();
                return outputBuilder.ToString();
            }
            else
            {
                p.WaitForExit(); // Ensure async stream reads complete
            }

            exitCode = p.ExitCode;
            error = errorBuilder.ToString();
            return outputBuilder.ToString();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return "";
        }
    }

    /// <summary>
    /// Runs a PowerShell script with UTF-8 encoding, asynchronously reading output.
    /// Uses -EncodedCommand to avoid shell quoting issues entirely.
    /// Kills the process if it exceeds the timeout.
    /// </summary>
    public static string RunPowerShell(string script, out string error, int timeoutMs = 30000)
    {
        string wrapped =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
            script;

        byte[] bytes = Encoding.Unicode.GetBytes(wrapped);
        string encoded = Convert.ToBase64String(bytes);
        string args = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        return Run("powershell.exe", args, out error, timeoutMs);
    }

    /// <summary>
    /// Starts PowerShell in a visible window with the given script, using -EncodedCommand
    /// to avoid all shell quoting issues. Useful for interactive commands (tracert, etc.).
    /// </summary>
    public static void StartPowerShellWindow(string script)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(script);
        string encoded = Convert.ToBase64String(bytes);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -EncodedCommand {encoded}",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Escapes a string for safe use inside single quotes in PowerShell.
    /// Returns the escaped string (without surrounding quotes).
    /// </summary>
    public static string EscapePsSingleQuoted(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return input.Replace("'", "''");
    }
}
