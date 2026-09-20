using System.Net;
using System.Text;
using WinNetManager.Core.Net;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class FirewallCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class FirewallManager
{
    private static readonly string[] ValidDirections = { "Inbound", "Outbound" };
    private static readonly string[] ValidActions = { "Allow", "Block" };
    private static readonly string[] ValidProtocols = { "TCP", "UDP", "Any" };
    private static readonly string[] ValidProfiles = { "Any", "Domain", "Private", "Public", "Domain,Private,Public" };

    public List<FirewallRuleInfo> GetInboundRules(bool winNetManagerOnly)
    {
        var rules = new List<FirewallRuleInfo>();
        string filter = winNetManagerOnly
            ? "Get-NetFirewallRule -Direction Inbound -DisplayName 'WinNetManager_*' -ErrorAction SilentlyContinue"
            : "Get-NetFirewallRule -Direction Inbound -ErrorAction SilentlyContinue";

        string script =
            "$rules = @(" + filter + "); " +
            "if ($rules.Count -eq 0) { return }; " +
            "foreach ($r in $rules) { " +
            "  $port = $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue; " +
            "  $iface = $r | Get-NetFirewallInterfaceFilter -ErrorAction SilentlyContinue; " +
            "  $addr = $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue; " +
            "  $ifaces = @($iface.InterfaceAlias | Where-Object { $_ -and $_ -ne 'Any' }); " +
            "  $locals = @($addr.LocalAddress | Where-Object { $_ -and $_ -ne 'Any' }); " +
            "  [pscustomobject]@{ " +
            "    Name = $r.Name; " +
            "    DisplayName = $r.DisplayName; " +
            "    Direction = [string]$r.Direction; " +
            "    Action = [string]$r.Action; " +
            "    Enabled = [string]$r.Enabled; " +
            "    Profile = [string]$r.Profile; " +
            "    Protocol = [string]$port.Protocol; " +
            "    LocalPort = @($port.LocalPort) -join ','; " +
            "    InterfaceAlias = $ifaces -join ','; " +
            "    LocalAddress = $locals -join ',' " +
            "  } " +
            "} | ConvertTo-Csv -NoTypeInformation";

        string error;
        int timeout = winNetManagerOnly ? 20000 : 60000;
        string output = ProcessRunner.RunPowerShell(script, out error, timeout);
        if (!string.IsNullOrEmpty(error) && !CI(error, "\u8b66\u544a") && !CI(error, "Warning") && !CI(error, "No MSFT_NetFirewallRule"))
            return rules;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return rules;

        string[] headers = CsvParser.ParseLine(lines[0]);
        int idxName = Array.IndexOf(headers, "Name");
        int idxDisplay = Array.IndexOf(headers, "DisplayName");
        int idxDir = Array.IndexOf(headers, "Direction");
        int idxAction = Array.IndexOf(headers, "Action");
        int idxEnabled = Array.IndexOf(headers, "Enabled");
        int idxProfile = Array.IndexOf(headers, "Profile");
        int idxProto = Array.IndexOf(headers, "Protocol");
        int idxPort = Array.IndexOf(headers, "LocalPort");
        int idxIface = Array.IndexOf(headers, "InterfaceAlias");
        int idxAddr = Array.IndexOf(headers, "LocalAddress");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = CsvParser.ParseLine(lines[i]);
            rules.Add(new FirewallRuleInfo
            {
                Name = Get(values, idxName),
                DisplayName = Get(values, idxDisplay),
                Direction = NormalizeDirection(Get(values, idxDir)),
                Action = NormalizeAction(Get(values, idxAction)),
                Enabled = NormalizeEnabled(Get(values, idxEnabled)),
                Profile = NormalizeProfile(Get(values, idxProfile)),
                Protocol = NormalizeProtocol(Get(values, idxProto)),
                LocalPort = NormalizeAny(Get(values, idxPort)),
                InterfaceAlias = NormalizeAny(Get(values, idxIface)),
                LocalAddress = NormalizeAny(Get(values, idxAddr)),
            });
        }

        return rules;
    }

    public static bool ValidateRule(FirewallRuleInfo rule, out string error)
    {
        error = "";
        if (rule == null)
        {
            error = "规则为空。";
            return false;
        }

        string display = rule.DisplayName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(display) || display.Any(char.IsControl) || display.IndexOfAny(new[] { '"', '\'', ';', '&', '|', '`', '$' }) >= 0)
        {
            error = "显示名称不能为空，且不能包含引号或命令分隔符。";
            return false;
        }
        rule.DisplayName = display;

        string direction = (rule.Direction ?? "").Trim();
        if (!ValidDirections.Contains(direction, StringComparer.OrdinalIgnoreCase))
        {
            error = "方向必须是 Inbound 或 Outbound。";
            return false;
        }
        rule.Direction = direction.Equals("Outbound", StringComparison.OrdinalIgnoreCase) ? "Outbound" : "Inbound";

        string action = (rule.Action ?? "").Trim();
        if (!ValidActions.Contains(action, StringComparer.OrdinalIgnoreCase))
        {
            error = "动作必须是 Allow 或 Block。";
            return false;
        }
        rule.Action = action.Equals("Block", StringComparison.OrdinalIgnoreCase) ? "Block" : "Allow";

        string protocol = (rule.Protocol ?? "TCP").Trim();
        if (!ValidProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase))
        {
            error = "协议必须是 TCP、UDP 或 Any。";
            return false;
        }
        rule.Protocol = protocol.ToUpperInvariant() == "ANY" ? "Any" : protocol.ToUpperInvariant();

        string profile = string.IsNullOrWhiteSpace(rule.Profile) ? "Any" : rule.Profile.Trim();
        if (!ValidProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            error = "配置文件无效。";
            return false;
        }
        rule.Profile = profile.Equals("Any", StringComparison.OrdinalIgnoreCase) ? "Any" : profile;

        string ports = rule.LocalPort?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(ports) || ports.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            rule.LocalPort = "Any";
        }
        else if (!FirewallPortSpec.TryParse(ports, out var parsed, out error))
        {
            return false;
        }
        else
        {
            rule.LocalPort = FirewallPortSpec.Join(parsed);
        }

        string alias = rule.InterfaceAlias?.Trim() ?? "";
        if (alias.Equals("Any", StringComparison.OrdinalIgnoreCase)) alias = "";
        if (alias.Any(char.IsControl) || alias.IndexOfAny(new[] { '"', ';', '&', '|', '`' }) >= 0)
        {
            error = "接口别名包含非法字符。";
            return false;
        }
        rule.InterfaceAlias = alias;

        string local = rule.LocalAddress?.Trim() ?? "";
        if (local.Equals("Any", StringComparison.OrdinalIgnoreCase)) local = "";
        if (!string.IsNullOrWhiteSpace(local))
        {
            var addrParts = local.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in addrParts)
            {
                string item = part.Trim();
                if (item.Contains('/'))
                {
                    int slash = item.LastIndexOf('/');
                    if (!IPAddress.TryParse(item[..slash], out _) || !int.TryParse(item[(slash + 1)..], out _))
                    {
                        error = "本地地址必须是有效 IP 或 CIDR。";
                        return false;
                    }
                }
                else if (!IPAddress.TryParse(item, out _))
                {
                    error = "本地地址必须是有效 IP 或 CIDR。";
                    return false;
                }
            }
            rule.LocalAddress = string.Join(",", addrParts.Select(a => a.Trim()));
        }
        else
        {
            rule.LocalAddress = "";
        }

        return true;
    }

    public FirewallCommandResult AddRule(FirewallRuleInfo rule)
    {
        if (!ValidateRule(rule, out string validationError))
            return new FirewallCommandResult { Success = false, Message = validationError };

        string script = BuildNewRuleScript(rule);
        return Execute(script, 20000);
    }

    public FirewallCommandResult DeleteRule(FirewallRuleInfo rule)
    {
        string name = (rule.Name ?? "").Trim();
        string display = (rule.DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(display))
            return new FirewallCommandResult { Success = false, Message = "找不到要删除的防火墙规则。" };

        string script;
        if (!string.IsNullOrWhiteSpace(name))
        {
            script = $"Remove-NetFirewallRule -Name '{ProcessRunner.EscapePsSingleQuoted(name)}' -ErrorAction Stop";
        }
        else
        {
            script = $"Remove-NetFirewallRule -DisplayName '{ProcessRunner.EscapePsSingleQuoted(display)}' -ErrorAction Stop";
        }
        return Execute(script, 15000);
    }

    public FirewallCommandResult SetEnabled(FirewallRuleInfo rule, bool enabled)
    {
        string name = (rule.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return new FirewallCommandResult { Success = false, Message = "找不到要删除的防火墙规则。" };
        string flag = enabled ? "True" : "False";
        string script = $"Set-NetFirewallRule -Name '{ProcessRunner.EscapePsSingleQuoted(name)}' -Enabled {flag} -ErrorAction Stop";
        return Execute(script, 15000);
    }

    public FirewallCommandResult ReplaceRule(FirewallRuleInfo original, FirewallRuleInfo updated)
    {
        var add = AddRule(updated);
        if (!add.Success)
            return add;

        var del = DeleteRule(original);
        if (!del.Success && !CI(del.Message, "找不到") && !CI(del.Message, "not found") && !CI(del.Message, "No MSFT_NetFirewallRule"))
        {
            DeleteRule(updated);
            return new FirewallCommandResult { Success = false, Message = "新规则已添加，但旧规则删除失败：" + del.Message };
        }
        return new FirewallCommandResult { Success = true };
    }

    public static string GetAddCommandPreview(FirewallRuleInfo rule)
    {
        if (!ValidateRule(rule, out _))
            return "";
        return BuildNewRuleScript(rule);
    }

    public static string GetDeleteCommandPreview(FirewallRuleInfo rule)
    {
        string name = (rule.Name ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return $"Remove-NetFirewallRule -Name '{ProcessRunner.EscapePsSingleQuoted(name)}'";
        return $"Remove-NetFirewallRule -DisplayName '{ProcessRunner.EscapePsSingleQuoted(rule.DisplayName)}'";
    }

    private static string BuildNewRuleScript(FirewallRuleInfo rule)
    {
        var sb = new StringBuilder();
        sb.Append("New-NetFirewallRule");
        sb.Append($" -DisplayName '{ProcessRunner.EscapePsSingleQuoted(rule.DisplayName)}'");
        sb.Append($" -Direction {rule.Direction}");
        sb.Append($" -Action {rule.Action}");
        sb.Append(" -Enabled True");
        sb.Append($" -Profile {FormatProfileArg(rule.Profile)}");
        sb.Append($" -Protocol {rule.Protocol}");
        if (!string.IsNullOrWhiteSpace(rule.LocalPort) && !rule.LocalPort.Equals("Any", StringComparison.OrdinalIgnoreCase)
            && !rule.Protocol.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            FirewallPortSpec.TryParse(rule.LocalPort, out var ports, out _);
            sb.Append($" -LocalPort {FirewallPortSpec.ToPowerShellArray(ports)}");
        }
        if (!string.IsNullOrWhiteSpace(rule.InterfaceAlias))
        {
            var ifaces = rule.InterfaceAlias.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => "'" + ProcessRunner.EscapePsSingleQuoted(a.Trim()) + "'");
            sb.Append($" -InterfaceAlias {string.Join(",", ifaces)}");
        }
        if (!string.IsNullOrWhiteSpace(rule.LocalAddress))
        {
            var addrs = rule.LocalAddress.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => "'" + ProcessRunner.EscapePsSingleQuoted(a.Trim()) + "'");
            sb.Append($" -LocalAddress {string.Join(",", addrs)}");
        }
        sb.Append(" -ErrorAction Stop");
        return sb.ToString();
    }

    private static string FormatProfileArg(string profile)
    {
        if (profile.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return "Any";
        var parts = profile.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim());
        return string.Join(",", parts);
    }

    private static FirewallCommandResult Execute(string script, int timeoutMs)
    {
        string error;
        ProcessRunner.RunPowerShell(script, out error, out int exitCode, timeoutMs);
        bool hasRealError = !string.IsNullOrWhiteSpace(error)
            && !CI(error, "警告")
            && !CI(error, "Warning");
        if (exitCode == 0 && !hasRealError)
            return new FirewallCommandResult { Success = true };

        string msg = (error ?? "").Trim();
        if (string.IsNullOrWhiteSpace(msg))
            msg = "命令执行失败（退出码 " + exitCode + "）。";
        if (CI(msg, "Access is denied") || CI(msg, "拒绝访问") || CI(msg, "requires elevation"))
            msg = "权限不足，需要以管理员身份运行。";
        else if (CI(msg, "already exists") || CI(msg, "已存在"))
            msg = "该防火墙规则已存在。";
        return new FirewallCommandResult { Success = false, Message = msg };
    }

    private static string Get(string[] values, int idx)
        => idx >= 0 && idx < values.Length ? values[idx].Trim() : "";

    private static string NormalizeDirection(string value)
    {
        if (value == "2" || CI(value, "Outbound")) return "Outbound";
        return "Inbound";
    }

    private static string NormalizeAction(string value)
    {
        if (value == "4" || CI(value, "Block")) return "Block";
        return "Allow";
    }

    private static string NormalizeEnabled(string value)
    {
        if (value == "2" || CI(value, "False") || CI(value, "Disabled")) return "False";
        return "True";
    }

    private static string NormalizeProtocol(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "256" || CI(value, "Any")) return "Any";
        if (value == "17" || CI(value, "UDP")) return "UDP";
        if (value == "6" || CI(value, "TCP")) return "TCP";
        return value;
    }

    private static string NormalizeProfile(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0" || CI(value, "Any")) return "Any";
        return value;
    }

    private static string NormalizeAny(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return "";
        return value;
    }

    private static bool CI(string source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
