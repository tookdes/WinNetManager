using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class DnsNrptManager
{
    private static string RunPowerShell(string script, out string error, int timeoutMs = 15000)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);

    public List<NrptRule> GetRules()
    {
        var rules = new List<NrptRule>();

        string script =
            "Get-DnsClientNrptRule | " +
            "Select-Object Name, Namespace, NameServers, Comment, GpoName | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = RunPowerShell(script, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return rules;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return rules;

        string[] headers = ParseCsvLine(lines[0]);
        int idxName = Array.IndexOf(headers, "Name");
        int idxNs = Array.IndexOf(headers, "Namespace");
        int idxServers = Array.IndexOf(headers, "NameServers");
        int idxComment = Array.IndexOf(headers, "Comment");
        int idxGpo = Array.IndexOf(headers, "GpoName");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 2) continue;

            string ns = idxNs >= 0 && idxNs < values.Length ? values[idxNs] : "";
            if (string.IsNullOrEmpty(ns)) continue;

            rules.Add(new NrptRule
            {
                Name = idxName >= 0 && idxName < values.Length ? values[idxName] : "",
                Namespace = ns,
                NameServers = idxServers >= 0 && idxServers < values.Length ? values[idxServers] : "",
                Comment = idxComment >= 0 && idxComment < values.Length ? values[idxComment] : "",
                GpoName = idxGpo >= 0 && idxGpo < values.Length ? values[idxGpo] : "",
            });
        }

        return rules;
    }

    public List<InterfaceDnsInfo> GetInterfaceDnsServers()
    {
        var list = new List<InterfaceDnsInfo>();

        string script =
            "Get-DnsClientServerAddress | " +
            "Select-Object InterfaceAlias, InterfaceIndex, AddressFamily, @{Name='ServerAddresses';Expression={[string]::Join(',', $_.ServerAddresses)}} | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = RunPowerShell(script, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return list;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return list;

        string[] headers = ParseCsvLine(lines[0]);
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxFamily = Array.IndexOf(headers, "AddressFamily");
        int idxServers = Array.IndexOf(headers, "ServerAddresses");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 2) continue;

            string alias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "";
            if (string.IsNullOrEmpty(alias)) continue;

            string family = idxFamily >= 0 && idxFamily < values.Length ? values[idxFamily] : "";
            if (family == "2") family = "IPv4";
            else if (family == "23") family = "IPv6";

            string servers = idxServers >= 0 && idxServers < values.Length ? values[idxServers] : "";

            list.Add(new InterfaceDnsInfo
            {
                InterfaceAlias = alias,
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                AddressFamily = family,
                ServerAddresses = servers
            });
        }

        return list;
    }

    public DnsResult SetInterfaceDns(string alias, string family, string servers)
    {
        var familyArg = family == "IPv6" ? "IPv6" : "IPv4";
        var sb = new StringBuilder();

        if (string.IsNullOrWhiteSpace(servers))
        {
            sb.Append($"Set-DnsClientServerAddress -InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(alias)}' -Family {familyArg} -ResetServerAddresses");
        }
        else
        {
            var serverParts = servers.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var escapedServers = serverParts.Select(p => $"'{ProcessRunner.EscapePsSingleQuoted(p)}'").ToList();
            sb.Append($"Set-DnsClientServerAddress -InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(alias)}' -Family {familyArg} -ServerAddresses {string.Join(",", escapedServers)}");
        }

        string error;
        RunPowerShell(sb.ToString(), out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DnsResult { Success = false, Message = TranslateError(error.Trim()) };

        return new DnsResult { Success = true };
    }

    public DnsResult AddRule(string ns, string dnsServers, string? comment = null)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return new DnsResult { Success = false, Message = "域名规则不能为空。" };

        // Validate and escape each DNS server address
        var serverParts = dnsServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (serverParts.Length == 0)
            return new DnsResult { Success = false, Message = "至少输入一个 DNS 服务器地址。" };
        var escapedServers = new List<string>();
        foreach (var part in serverParts)
        {
            if (!System.Net.IPAddress.TryParse(part, out _))
                return new DnsResult { Success = false, Message = $"无效的 DNS 服务器地址：{part}" };
            escapedServers.Add($"'{ProcessRunner.EscapePsSingleQuoted(part)}'");
        }

        var sb = new StringBuilder();
        sb.Append($"Add-DnsClientNrptRule -Namespace '{ProcessRunner.EscapePsSingleQuoted(ns)}' -NameServers {string.Join(",", escapedServers)}");
        if (!string.IsNullOrEmpty(comment))
            sb.Append($" -Comment '{ProcessRunner.EscapePsSingleQuoted(comment)}'");

        string error;
        RunPowerShell(sb.ToString(), out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DnsResult { Success = false, Message = TranslateError(error.Trim()) };

        return new DnsResult { Success = true };
    }

    public DnsResult DeleteRule(string name, string gpoName)
    {
        var sb = new StringBuilder();
        sb.Append($"Remove-DnsClientNrptRule -Name '{ProcessRunner.EscapePsSingleQuoted(name)}' -Confirm:$false");
        if (!string.IsNullOrEmpty(gpoName))
            sb.Append($" -GpoName '{ProcessRunner.EscapePsSingleQuoted(gpoName)}'");

        string error;
        RunPowerShell(sb.ToString(), out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DnsResult { Success = false, Message = TranslateError(error.Trim()) };

        return new DnsResult { Success = true };
    }

    public DnsResult FlushCache()
    {
        string error;
        RunPowerShell("Clear-DnsClientCache", out error, 10000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DnsResult { Success = false, Message = error.Trim() };

        return new DnsResult { Success = true };
    }

    public DnsResolveResult ResolveDomain(string domain)
    {
        string script = $"Resolve-DnsName '{ProcessRunner.EscapePsSingleQuoted(domain)}' | Select-Object Type, IPAddress, NameHost | ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DnsResolveResult { Success = false, Message = error.Trim() };

        var records = new List<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2)
        {
            string[] headers = ParseCsvLine(lines[0]);
            int idxType = Array.IndexOf(headers, "Type");
            int idxIp = Array.IndexOf(headers, "IPAddress");
            int idxHost = Array.IndexOf(headers, "NameHost");

            for (int i = 1; i < lines.Length; i++)
            {
                string[] values = ParseCsvLine(lines[i]);
                if (values.Length < 2) continue;

                string type = idxType >= 0 && idxType < values.Length ? values[idxType] : "";
                string ip = idxIp >= 0 && idxIp < values.Length ? values[idxIp] : "";
                string host = idxHost >= 0 && idxHost < values.Length ? values[idxHost] : "";

                if (!string.IsNullOrEmpty(ip))
                    records.Add($"[{type}] {ip}");
                else if (!string.IsNullOrEmpty(host))
                    records.Add($"[{type}] {host}");
            }
        }

        return new DnsResolveResult
        {
            Success = true,
            Records = records,
            Message = $"解析到 {records.Count} 条记录",
        };
    }

    private static string TranslateError(string error)
    {
        if (error.Contains("Access is denied") || error.Contains("拒绝访问") || error.Contains("权限"))
            return "权限不足，需要以管理员身份运行。";
        if (error.Contains("already exists") || error.Contains("已存在"))
            return "该 NRPT 规则已存在。";
        if (error.Contains("not found") || error.Contains("找不到"))
            return "找不到指定的 NRPT 规则。";
        return error;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result.ToArray();
    }
}

public class DnsResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class DnsResolveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<string> Records { get; set; } = new();
}
