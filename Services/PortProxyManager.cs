using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class PortProxyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class PortProxyManager
{
    private static readonly string[] Directions = { "v4tov4", "v6tov4", "v4tov6", "v6tov6" };

    private static string RunCommand(string fileName, string arguments, out string error, int timeoutMs = 15000)
        => ProcessRunner.Run(fileName, arguments, out error, timeoutMs);

    public List<PortProxyRule> GetRules()
    {
        var rules = new List<PortProxyRule>();
        var dataRegex = new Regex(@"^(\S+)\s+(\d+)\s+(\S+)\s+(\d+)\s*$");

        foreach (var direction in Directions)
        {
            string error;
            string output = RunCommand("netsh", $"interface portproxy show {direction}", out error);

            if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
                continue;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool inDataSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // 分隔线标志着数据区域开始
                if (trimmed.StartsWith("---") || trimmed.StartsWith("==="))
                {
                    inDataSection = true;
                    continue;
                }

                if (!inDataSection) continue;

                var match = dataRegex.Match(trimmed);
                if (match.Success)
                {
                    rules.Add(new PortProxyRule
                    {
                        Direction = direction,
                        ListenAddress = match.Groups[1].Value,
                        ListenPort = match.Groups[2].Value,
                        ConnectAddress = match.Groups[3].Value,
                        ConnectPort = match.Groups[4].Value,
                        Protocol = "tcp"
                    });
                }
            }
        }

        return rules;
    }

    public PortProxyResult AddRule(PortProxyRule rule)
    {
        if (!ValidateRule(rule, out string valErr))
            return new PortProxyResult { Success = false, Message = valErr };

        string args = $"interface portproxy add {rule.Direction} " +
            $"listenaddress=\"{rule.ListenAddress}\" listenport=\"{rule.ListenPort}\" " +
            $"connectaddress=\"{rule.ConnectAddress}\" connectport=\"{rule.ConnectPort}\" " +
            $"protocol=\"{rule.Protocol}\"";

        string error;
        RunCommand("netsh", args, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            string msg = TranslateError(error.Trim());
            return new PortProxyResult { Success = false, Message = msg };
        }

        return new PortProxyResult { Success = true };
    }

    public PortProxyResult SetRule(PortProxyRule rule)
    {
        if (!ValidateRule(rule, out string valErr))
            return new PortProxyResult { Success = false, Message = valErr };

        string args = $"interface portproxy set {rule.Direction} " +
            $"listenaddress=\"{rule.ListenAddress}\" listenport=\"{rule.ListenPort}\" " +
            $"connectaddress=\"{rule.ConnectAddress}\" connectport=\"{rule.ConnectPort}\" " +
            $"protocol=\"{rule.Protocol}\"";

        string error;
        RunCommand("netsh", args, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            string msg = TranslateError(error.Trim());
            return new PortProxyResult { Success = false, Message = msg };
        }

        return new PortProxyResult { Success = true };
    }

    public PortProxyResult DeleteRule(PortProxyRule rule)
    {
        string args = $"interface portproxy delete {rule.Direction} " +
            $"listenaddress=\"{rule.ListenAddress}\" listenport=\"{rule.ListenPort}\"";

        string error;
        RunCommand("netsh", args, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            string msg = TranslateError(error.Trim());
            return new PortProxyResult { Success = false, Message = msg };
        }

        return new PortProxyResult { Success = true };
    }

    public PortProxyResult ResetAll()
    {
        string error;
        RunCommand("netsh", "interface portproxy reset", out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            return new PortProxyResult { Success = false, Message = error.Trim() };
        }

        return new PortProxyResult { Success = true };
    }

    // --- 防火墙联动 ---

    public static string GetFirewallRuleName(PortProxyRule rule)
    {
        var safeAddr = (rule.ListenAddress ?? "").Replace("\"", "'");
        return $"WinNetManager_PortProxy_{safeAddr}_{rule.ListenPort}";
    }

    /// <summary>
    /// Validates a port proxy rule for injection-safety and basic format correctness.
    /// </summary>
    private static readonly string[] ValidDirections = { "v4tov4", "v4tov6", "v6tov4", "v6tov6" };

    public static bool ValidateRule(PortProxyRule rule, out string error)
    {
        error = "";
        if (!ValidDirections.Contains(rule.Direction))
        {
            error = $"方向无效：{rule.Direction}";
            return false;
        }
        if (rule.Protocol != "tcp")
        {
            error = $"协议无效：{rule.Protocol}（仅支持 tcp）";
            return false;
        }
        if (string.IsNullOrEmpty(rule.ListenAddress) || !IsValidAddress(rule.ListenAddress))
        {
            error = $"监听地址无效：{rule.ListenAddress}";
            return false;
        }
        if (string.IsNullOrEmpty(rule.ConnectAddress) || !IsValidAddress(rule.ConnectAddress))
        {
            error = $"目标地址无效：{rule.ConnectAddress}";
            return false;
        }
        if (!int.TryParse(rule.ListenPort, out int lp) || lp <= 0 || lp > 65535)
        {
            error = $"监听端口无效：{rule.ListenPort}";
            return false;
        }
        if (!int.TryParse(rule.ConnectPort, out int cp) || cp <= 0 || cp > 65535)
        {
            error = $"目标端口无效：{rule.ConnectPort}";
            return false;
        }
        if (System.Net.IPAddress.TryParse(rule.ListenAddress, out System.Net.IPAddress? listenIp)
            && System.Net.IPAddress.TryParse(rule.ConnectAddress, out System.Net.IPAddress? connectIp)
            && listenIp?.Equals(connectIp) == true
            && lp == cp)
        {
            error = "监听地址和端口不能与目标地址和端口相同";
            return false;
        }
        return true;
    }

    private static bool IsValidAddress(string address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        if (address.IndexOfAny(new[] { '\"', ';', '&', '|', '<', '>', '(', ')' }) >= 0)
            return false;
        return System.Net.IPAddress.TryParse(address, out _);
    }

    public PortProxyResult AddFirewallRule(PortProxyRule rule)
    {
        string name = GetFirewallRuleName(rule);
        string args = $"advfirewall firewall add rule name=\"{name}\" " +
            $"dir=in action=allow protocol={rule.Protocol} localport={rule.ListenPort}";

        string error;
        RunCommand("netsh", args, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            return new PortProxyResult { Success = false, Message = TranslateFirewallError(error.Trim()) };
        }

        return new PortProxyResult { Success = true };
    }

    public PortProxyResult DeleteFirewallRule(PortProxyRule rule)
    {
        string name = GetFirewallRuleName(rule);
        string args = $"advfirewall firewall delete rule name=\"{name}\"";

        string error;
        RunCommand("netsh", args, out error);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            return new PortProxyResult { Success = false, Message = TranslateFirewallError(error.Trim()) };
        }

        return new PortProxyResult { Success = true };
    }

    public PortProxyResult UpdateFirewallRule(PortProxyRule oldRule, PortProxyRule newRule)
    {
        var delRes = DeleteFirewallRule(oldRule);

        // 删除失败且不是因为规则不存在，报告错误
        if (!delRes.Success && !delRes.Message.Contains("找不到"))
            return delRes;

        var addRes = AddFirewallRule(newRule);
        if (!addRes.Success)
            return addRes;

        return new PortProxyResult { Success = true };
    }

    /// <summary>
    /// 检查 IP Helper 服务 (iphlpsvc) 是否正在运行。
    /// </summary>
    public bool IsServiceRunning()
    {
        try
        {
            string error;
            string output = RunCommand("sc", "query iphlpsvc", out error, 5000);
            return output.Contains("RUNNING") || output.Contains("正在运行");
        }
        catch
        {
            return false;
        }
    }

    private static string TranslateError(string error)
    {
        if (error.Contains("Element not found") || error.Contains("找不到元素") || error.Contains("找不到"))
            return "找不到指定的端口转发规则。";
        if (error.Contains("already exists") || error.Contains("已存在"))
            return "该端口转发规则已存在。";
        if (error.Contains("Access is denied") || error.Contains("拒绝访问") || error.Contains("权限"))
            return "权限不足，需要以管理员身份运行。";
        if (error.Contains("requires elevation"))
            return "需要以管理员身份运行。";
        if (error.Contains("Invalid parameter") || error.Contains("参数无效") || error.Contains("参数"))
            return "参数无效，请检查地址和端口格式。";
        return error;
    }

    private static string TranslateFirewallError(string error)
    {
        if (error.Contains("Access is denied") || error.Contains("拒绝访问") || error.Contains("权限"))
            return "权限不足，无法修改防火墙规则。";
        if (error.Contains("requires elevation"))
            return "需要以管理员身份运行。";
        if (error.Contains("No rules match") || error.Contains("找不到"))
            return "找不到指定的防火墙规则。";
        return error;
    }
}
