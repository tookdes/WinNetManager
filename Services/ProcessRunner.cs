using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
                    error += Environment.NewLine + CleanCliXml(timeoutError).Trim();
                return outputBuilder.ToString();
            }
            else
            {
                p.WaitForExit(); // Ensure async stream reads complete
            }

            exitCode = p.ExitCode;
            error = CleanCliXml(errorBuilder.ToString());
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
            "$ProgressPreference = 'SilentlyContinue'; " +
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

    /// <summary>
    /// Cleans PowerShell CLIXML error output into readable text.
    /// PowerShell writes CLIXML to stderr when -EncodedCommand is used.
    /// Extracts S[@S='Error'] nodes and decodes _x000D_x000A_ sequences.
    /// Falls back to regex extraction if XML parsing fails.
    /// </summary>
    private static string CleanCliXml(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // Strip leading "#< CLIXML" line that PowerShell 5.1 prepends
        raw = Regex.Replace(raw, @"^#\<\s*CLIXML\s*", "", RegexOptions.Multiline).TrimStart();

        if (!raw.Contains("<Objs")) return raw;

        // Try regex extract Error nodes directly (fast and namespace-agnostic)
        var matches = Regex.Matches(raw, @"<S\s+S=""Error"">([^<]+)</S>");
        if (matches.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (Match m in matches)
                sb.AppendLine(DecodeCliXmlChars(m.Groups[1].Value));
            return sb.ToString().Trim();
        }

        // Try XML parsing as fallback
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(raw);
            var sb = new StringBuilder();
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("ps", "http://schemas.microsoft.com/powershell/2004/04");
            var nodes = doc.SelectNodes("//ps:S[@S='Error']", nsmgr);
            if (nodes != null)
            {
                foreach (XmlNode node in nodes)
                {
                    string text = node.InnerText ?? "";
                    sb.AppendLine(DecodeCliXmlChars(text));
                }
            }
            if (sb.Length > 0) return sb.ToString().Trim();
        }
        catch { }

        // Last resort: strip all XML tags
        return Regex.Replace(raw, @"<[^>]+>", "").Trim();
    }

    private static string DecodeCliXmlChars(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Match sequence of encoded chars like _x000D_x000A_ or _x005F_
        // The pattern matches a leading _x, followed by 4 hex digits, followed by any number of _xHHHH sequences, ending with a trailing _
        return Regex.Replace(text, @"_x([0-9A-Fa-f]{4}(?:_x[0-9A-Fa-f]{4})*)_", m =>
        {
            string inner = m.Groups[1].Value;
            string[] hexBlocks = inner.Split(new[] { "_x" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            foreach (var hex in hexBlocks)
            {
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                {
                    sb.Append(code == 0x5F ? "_" : ((char)code).ToString());
                }
            }
            return sb.ToString();
        });
    }
}
