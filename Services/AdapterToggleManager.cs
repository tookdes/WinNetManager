using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class AdapterToggleManager
{
    /// <summary>
    /// Reads per-adapter toggle states: IPv6 binding, IPv6 privacy (RandomizeIdentifiers),
    /// NetBIOS over TCP/IP.
    /// </summary>
    public List<AdapterToggleInfo> GetAdapterToggles()
    {
        var result = new List<AdapterToggleInfo>();

        // 1) Get adapter names + IPv6 binding state
        string bindingScript =
            "Get-NetAdapterBinding -ComponentId ms_tcpip6 -ErrorAction SilentlyContinue | " +
            "Select-Object Name, Enabled | ConvertTo-Csv -NoTypeInformation";
        string bindErr;
        string bindOut = ProcessRunner.RunPowerShell(bindingScript, out bindErr, 15000);

        var ipv6Map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseCsvOutput(bindOut))
        {
            if (line.TryGetValue("Name", out var name) && line.TryGetValue("Enabled", out var en))
                ipv6Map[name] = IsTrue(en);
        }

        // 2) Get per-interface RandomizeIdentifiers (IPv6 privacy extensions)
        //    This property is on Get-NetIPInterface -AddressFamily IPv6
        string privacyScript =
            "Get-NetIPInterface -AddressFamily IPv6 -ErrorAction SilentlyContinue | " +
            "Select-Object InterfaceAlias, RandomizeIdentifiers | ConvertTo-Csv -NoTypeInformation";
        string privErr;
        string privOut = ProcessRunner.RunPowerShell(privacyScript, out privErr, 15000);

        // RandomizeIdentifiers can be: Enabled, Disabled, or a numeric value
        var privacyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseCsvOutput(privOut))
        {
            if (line.TryGetValue("InterfaceAlias", out var alias) && line.TryGetValue("RandomizeIdentifiers", out var ri))
                privacyMap[alias] = ri;
        }

        // 3) Get NetBIOS setting per adapter from registry (WMI is unreliable in .NET 8)
        //    NetbiosOptions: 0=Default(DHCP), 1=Enabled, 2=Disabled
        string nbScript =
            "Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NetBT\\Parameters\\Interfaces' -ErrorAction SilentlyContinue | " +
            "ForEach-Object { " +
            "  $tcpGuid = $_.PSChildName; " +
            "  $nb = (Get-ItemProperty -LiteralPath $_.PSPath -Name 'NetbiosOptions' -ErrorAction SilentlyContinue).NetbiosOptions; " +
            "  [PSCustomObject]@{ TcpGuid = $tcpGuid; NetbiosOptions = $nb } " +
            "} | ConvertTo-Csv -NoTypeInformation";
        string nbErr;
        string nbOut = ProcessRunner.RunPowerShell(nbScript, out nbErr, 15000);

        var nbMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseCsvOutput(nbOut))
        {
            if (line.TryGetValue("TcpGuid", out var guid) && line.TryGetValue("NetbiosOptions", out var opt))
            {
                if (int.TryParse(opt, out int val))
                    nbMap[guid] = val;
            }
        }

        // Map TCP interface GUIDs to adapter names
        string guidMapScript =
            "Get-NetAdapter -ErrorAction SilentlyContinue | " +
            "Select-Object Name, InterfaceGuid, Status, InterfaceDescription | ConvertTo-Csv -NoTypeInformation";
        string gmErr;
        string gmOut = ProcessRunner.RunPowerShell(guidMapScript, out gmErr, 15000);

        foreach (var line in ParseCsvOutput(gmOut))
        {
            if (!line.TryGetValue("Name", out var adapterName)) continue;
            line.TryGetValue("InterfaceGuid", out var ifGuid);
            line.TryGetValue("Status", out var status);
            line.TryGetValue("InterfaceDescription", out var desc);

            bool ipv6Enabled = ipv6Map.TryGetValue(adapterName, out var v6) && v6;

            string privacyState = "N/A";
            if (privacyMap.TryGetValue(adapterName, out var ps))
                privacyState = ps;

            int nbOpt = 0; // default
            if (!string.IsNullOrEmpty(ifGuid))
            {
                string tcpKey = "Tcpip_" + ifGuid.Trim('{', '}');
                if (nbMap.TryGetValue(tcpKey, out var nbVal))
                    nbOpt = nbVal;
            }

            result.Add(new AdapterToggleInfo
            {
                Name = adapterName,
                Description = desc ?? "",
                Status = status ?? "",
                IPv6Enabled = ipv6Enabled,
                IPv6PrivacyState = privacyState,
                NetBiosOption = nbOpt,
            });
        }

        result.Sort((a, b) => NaturalStringComparer.CompareStrings(a.Name, b.Name));
        return result;
    }

    // ── Global IPv6 privacy ──────────────────────────────────────────

    public DhcpResult GetGlobalIPv6Privacy()
    {
        string script = "Get-NetIPv6Protocol | Select-Object RandomizeIdentifiers | ConvertTo-Csv -NoTypeInformation";
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 10000);
        foreach (var line in ParseCsvOutput(output))
        {
            if (line.TryGetValue("RandomizeIdentifiers", out var ri))
                return new DhcpResult { Success = true, Message = ri };
        }
        return new DhcpResult { Success = false, Message = error };
    }

    public DhcpResult SetGlobalIPv6Privacy(bool enabled)
    {
        string flag = enabled ? "Enabled" : "Disabled";
        string script = $"Set-NetIPv6Protocol -RandomizeIdentifiers {flag}";
        return RunPs(script);
    }

    public static string GetGlobalIPv6PrivacyCommandPreview(bool enabled)
    {
        string flag = enabled ? "Enabled" : "Disabled";
        return $"Set-NetIPv6Protocol -RandomizeIdentifiers {flag}";
    }

    // ── Per-adapter IPv6 privacy ─────────────────────────────────────

    public DhcpResult SetIPv6Privacy(string adapterName, bool enabled)
    {
        string flag = enabled ? "Enabled" : "Disabled";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' " +
            $"-AddressFamily IPv6 " +
            $"-RandomizeIdentifiers {flag}";
        return RunPs(script);
    }

    public static string GetIPv6PrivacyCommandPreview(string adapterName, bool enabled)
    {
        string flag = enabled ? "Enabled" : "Disabled";
        return $"Set-NetIPInterface -InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' -AddressFamily IPv6 -RandomizeIdentifiers {flag}";
    }

    // ── IPv6 binding on/off ──────────────────────────────────────────

    public DhcpResult SetIPv6Binding(string adapterName, bool enabled)
    {
        string verb = enabled ? "Enable" : "Disable";
        string script = $"{verb}-NetAdapterBinding -Name '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' -ComponentId ms_tcpip6";
        return RunPs(script);
    }

    public static string GetIPv6BindingCommandPreview(string adapterName, bool enabled)
    {
        string verb = enabled ? "Enable" : "Disable";
        return $"{verb}-NetAdapterBinding -Name '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' -ComponentId ms_tcpip6";
    }

    // ── NetBIOS over TCP/IP ──────────────────────────────────────────

    public DhcpResult SetNetBios(string adapterName, int option)
    {
        // option: 0=Default(DHCP), 1=Enabled, 2=Disabled
        // We need to find the interface GUID first, then set registry
        string alias = ProcessRunner.EscapePsSingleQuoted(adapterName);
        string script =
            $"$guid = (Get-NetAdapter -Name '{alias}' -ErrorAction Stop).InterfaceGuid.Trim('{{','}}'); " +
            $"$path = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NetBT\\Parameters\\Interfaces\\Tcpip_$guid\"; " +
            $"Set-ItemProperty -LiteralPath $path -Name 'NetbiosOptions' -Value {option} -Type DWord";
        return RunPs(script);
    }

    public static string GetNetBiosCommandPreview(string adapterName, int option)
    {
        string optName = option switch { 0 => "Default (DHCP)", 1 => "Enabled", 2 => "Disabled", _ => option.ToString() };
        return $"# NetBIOS over TCP/IP -> {optName}\n" +
               $"$guid = (Get-NetAdapter -Name '{ProcessRunner.EscapePsSingleQuoted(adapterName)}').InterfaceGuid.Trim('{{','}}')\n" +
               $"Set-ItemProperty 'HKLM:\\...\\NetBT\\Parameters\\Interfaces\\Tcpip_$guid' -Name NetbiosOptions -Value {option} -Type DWord";
    }

    // ── Use temporary IPv6 addresses (global) ────────────────────────

    public DhcpResult GetGlobalUseTemporaryAddresses()
    {
        string script = "Get-NetIPv6Protocol | Select-Object UseTemporaryAddresses | ConvertTo-Csv -NoTypeInformation";
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 10000);
        foreach (var line in ParseCsvOutput(output))
        {
            if (line.TryGetValue("UseTemporaryAddresses", out var val))
                return new DhcpResult { Success = true, Message = val };
        }
        return new DhcpResult { Success = false, Message = error };
    }

    public DhcpResult SetGlobalUseTemporaryAddresses(bool enabled)
    {
        string flag = enabled ? "Always" : "Disabled";
        string script = $"Set-NetIPv6Protocol -UseTemporaryAddresses {flag}";
        return RunPs(script);
    }

    public static string GetGlobalUseTemporaryAddressesCommandPreview(bool enabled)
    {
        string flag = enabled ? "Always" : "Disabled";
        return $"Set-NetIPv6Protocol -UseTemporaryAddresses {flag}";
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static DhcpResult RunPs(string script)
    {
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "\u8b66\u544a") && !CI(error, "Warning"))
        {
            string msg = error.Trim();
            if (CI(msg, "requires elevation") || CI(msg, "Access is denied") || CI(msg, "\u62d2\u7edd\u8bbf\u95ee"))
                msg = "\u9519\u8bef\uff1a\u9700\u8981\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u8fd0\u884c\u672c\u7a0b\u5e8f\u3002";
            return new DhcpResult { Success = false, Message = msg };
        }
        return new DhcpResult { Success = true, Message = output?.Trim() ?? "" };
    }

    private static List<Dictionary<string, string>> ParseCsvOutput(string output)
    {
        var result = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;

        string[] headers = CsvParser.ParseLine(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = CsvParser.ParseLine(lines[i]);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < headers.Length && j < values.Length; j++)
                dict[headers[j]] = values[j];
            result.Add(dict);
        }
        return result;
    }

    private static bool IsTrue(string value)
        => value == "1"
            || value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);

    private static bool CI(string source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
