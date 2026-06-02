using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinNetManager.Services;

public class NetworkHealthItem
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    // None/Info/Warn/Danger
    public string Risk { get; set; } = "None";
    public string RiskDisplay => Risk switch
    {
        "Danger" => "!!!",
        "Warn" => "!",
        "Info" => "i",
        _ => ""
    };
}

public static class NetworkHealthService
{
    public static List<NetworkHealthItem> GetSnapshot()
    {
        var items = new List<NetworkHealthItem>();

        try { CollectDrivers(items); } catch { }
        try { CollectTun(items); } catch { }
        try { CollectWfpAndHook(items); } catch { }
        try { CollectProxy(items); } catch { }
        try { CollectVpn(items); } catch { }
        try { CollectFirewall(items); } catch { }
        try { CollectServices(items); } catch { }
        try { CollectDns(items); } catch { }
        try { CollectHosts(items); } catch { }

        // Risk-sort: Danger > Warn > Info > None
        var order = new Dictionary<string, int> { ["Danger"] = 0, ["Warn"] = 1, ["Info"] = 2, ["None"] = 3 };
        items.Sort((a, b) =>
        {
            int oa = order.TryGetValue(a.Risk, out int va) ? va : 9;
            int ob = order.TryGetValue(b.Risk, out int vb) ? vb : 9;
            int c = oa.CompareTo(ob);
            return c != 0 ? c : string.Compare(a.Category + a.Name, b.Category + b.Name, StringComparison.Ordinal);
        });

        return items;
    }

    // ------------------------------ Collectors ------------------------------

    private static void CollectDrivers(List<NetworkHealthItem> items)
    {
        // WinDivert — 用 driverquery 确认驱动是否真正在内核中加载，避免 sc query 状态残留误报
        string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string? windivertFile = new[]
        {
            Path.Combine(sysDir, "drivers", "WinDivert64.sys"),
            Path.Combine(sysDir, "drivers", "WinDivert32.sys"),
            Path.Combine(sysDir, "drivers", "WinDivert.sys")
        }.FirstOrDefault(File.Exists);

        bool driverLoaded = IsDriverLoaded("WinDivert");

        if (driverLoaded)
        {
            // 驱动确实在内核中运行 — 这才是真正的风险
            items.Add(new NetworkHealthItem
            {
                Category = "网络过滤驱动",
                Name = "WinDivert",
                Status = "运行中(驱动已加载)",
                Detail = "WinDivert 驱动正在内核中运行，可能劫持或丢弃流量。检查是否有加速器/抓包工具在使用。",
                Risk = "Danger"
            });
        }
        else if (windivertFile != null)
        {
            // 驱动文件存在但未加载 — 残留
            items.Add(new NetworkHealthItem
            {
                Category = "网络过滤驱动",
                Name = "WinDivert",
                Status = "驱动文件残留(未加载)",
                Detail = $"驱动文件 {Path.GetFileName(windivertFile)} 存在于 drivers 目录但未在内核中运行。" +
                         $"如确认不再使用，可手动删除：{windivertFile}",
                Risk = "Info"
            });
        }
        else if (ServiceExists("WinDivert") || ServiceExists("WinDivert14"))
        {
            items.Add(new NetworkHealthItem
            {
                Category = "网络过滤驱动",
                Name = "WinDivert",
                Status = "服务注册残留",
                Detail = "驱动文件已不存在，但服务注册表项残留。可用 sc delete WinDivert 清理。",
                Risk = "None"
            });
        }

        // Npcap / WinPcap
        bool npcap = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Npcap"));
        bool winpcap = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "npf.sys"));
        if (npcap || winpcap)
        {
            items.Add(new NetworkHealthItem
            {
                Category = "抓包驱动",
                Name = npcap ? "Npcap" : "WinPcap",
                Status = "已安装",
                Detail = "抓包/VPN/部分游戏工具依赖。通常安全，但异常版本可能影响网络。",
                Risk = "Info"
            });
        }
    }

    private static void CollectTun(List<NetworkHealthItem> items)
    {
        // Well-known TUN adapters / services — 已安装未运行的给出残留位置
        var tunNames = new (string svc, string display, string driverFile, string uninstall)[]
        {
            ("WireGuard", "WireGuard", "wireguard.sys", "控制面板 → 程序 → 卸载 WireGuard"),
            ("Tailscale", "Tailscale", "tailscale.sys", "控制面板 → 程序 → 卸载 Tailscale"),
            ("ZeroTierOneService", "ZeroTier", "zttap3.sys", "控制面板 → 程序 → 卸载 ZeroTier One；残留驱动：C:\\ProgramData\\ZeroTier\\One"),
            ("Nebula", "Nebula", "wintun.sys", "控制面板 → 程序 → 卸载 Nebula"),
            ("CloudflareWARP", "Cloudflare WARP", "WinTun.sys", "控制面板 → 程序 → 卸载 Cloudflare WARP；或运行 warp-cli delete"),
            ("Wintun", "WinTUN", "wintun.sys", "WinTUN 是 WireGuard 的底层驱动，卸载 WireGuard 即可"),
            ("tap0901", "TAP-Windows (OpenVPN)", "tap0901.sys", "控制面板 → 程序 → 卸载 TAP-Windows；或 OpenVPN 安装目录 uninstall"),
            ("tapwindows", "TAP-Windows", "tapwindows.sys", "控制面板 → 程序 → 卸载 TAP-Windows"),
        };

        foreach (var t in tunNames)
        {
            bool svcExists = ServiceExists(t.svc);
            bool adapterFound = AdapterNameContains(t.display) || AdapterNameContains(t.svc);
            if (!svcExists && !adapterFound) continue;

            bool running = ServiceRunning(t.svc) || IsDriverLoaded(t.svc);
            string st = running ? "运行中" : (svcExists ? "已安装(未运行)" : "检测到适配器");

            // 给出具体的残留位置和卸载方法
            string detail = $"驱动文件：{t.driverFile}  |  服务：{t.svc}。";
            if (!running)
                detail += $" 如不再使用，卸载方法：{t.uninstall}。";

            items.Add(new NetworkHealthItem
            {
                Category = "虚拟网卡/TUN",
                Name = t.display,
                Status = st,
                Detail = detail,
                Risk = running ? "Info" : "None"
            });
        }

        // Generic TAP/TUN adapters
        var tapAdapters = GetAdapterNames().Where(n =>
            n.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("TUN", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in tapAdapters)
        {
            if (items.Any(i => i.Category == "虚拟网卡/TUN" && a.Contains(i.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            items.Add(new NetworkHealthItem
            {
                Category = "虚拟网卡/TUN",
                Name = a,
                Status = "已检测到",
                Detail = "TAP/TUN 类虚拟适配器，可能由第三方 VPN 或加速器创建。可在「网卡跃点」标签页查看其接口跃点。",
                Risk = "Info"
            });
        }
    }

    private static void CollectWfpAndHook(List<NetworkHealthItem> items)
    {
        // WFP lightweight filters
        var wfpDrivers = new[] { "WfpLwf", "wfplwft", "NdisImPlatform", "VfpExt" };
        foreach (var d in wfpDrivers)
        {
            if (!ServiceExists(d)) continue;
            string st = ServiceRunning(d) ? "运行中" : "已安装(未运行)";
            items.Add(new NetworkHealthItem
            {
                Category = "WFP/NDIS 过滤",
                Name = d,
                Status = st,
                Detail = "Windows Filtering Platform / NDIS 轻量级过滤驱动，安全软件和部分加速器使用。",
                Risk = "Info"
            });
        }

        // Known security/monitoring WFP callout drivers
        var knownWfp = new[] { "WdNisDrv", "MsSecFlt", "bndef", "bfs", "klflt", "kltdi", "klwfp", "klids" };
        foreach (var d in knownWfp)
        {
            if (!ServiceExists(d)) continue;
            string st = ServiceRunning(d) ? "运行中" : "已安装(未运行)";
            items.Add(new NetworkHealthItem
            {
                Category = "WFP/NDIS 过滤",
                Name = d,
                Status = st,
                Detail = "已知安全/监控类过滤驱动。",
                Risk = "Info"
            });
        }

        // AppInit_DLLs (global hook injection)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (key != null)
            {
                string? appInit = key.GetValue("AppInit_DLLs") as string;
                bool loadAppInit = key.GetValue("LoadAppInit_DLLs") is int v && v == 1;
                if (loadAppInit && !string.IsNullOrWhiteSpace(appInit))
                {
                    items.Add(new NetworkHealthItem
                    {
                        Category = "全局注入/Hook",
                        Name = "AppInit_DLLs",
                        Status = "已启用",
                        Detail = $"值：{appInit.Trim()}。所有加载 user32.dll 的进程都会被注入，可能影响网络组件。",
                        Risk = "Warn"
                    });
                }
            }
        }
        catch { }

        // LSP / Winsock providers — 用子串匹配过滤系统标准组件（注册表值格式可能是 "MSAFD Tcpip [TCP/IP]" 而非纯 "Tcpip"）
        var systemKeywords = new[]
        {
            "tcp", "udp", "irda", "vmbus", "psched", "afunix", "rfcomm",
            "mswsock", "rsvpsp", "nwlnkipx", "nwlnkflt", "pnrp",
            "msafd", "mstcp", "tcpip", "tcpip6", "蓝牙", "bluetooth"
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Winsock\Parameters");
            if (key != null)
            {
                var providers = key.GetValue("Transports") as string[];
                if (providers != null && providers.Length > 0)
                {
                    var nonStandard = providers.Where(p =>
                    {
                        var t = p.Trim().ToLowerInvariant();
                        return !systemKeywords.Any(k => t.Contains(k));
                    }).ToList();
                    if (nonStandard.Count > 0)
                    {
                        items.Add(new NetworkHealthItem
                        {
                            Category = "Winsock/LSP",
                            Name = "第三方 Winsock 提供商",
                            Status = $"{nonStandard.Count} 个",
                            Detail = "非系统标准的 Winsock 提供商：" + string.Join("; ", nonStandard.Take(5))
                                   + "。可在注册表 HKLM\\SYSTEM\\CurrentControlSet\\Services\\Winsock\\Parameters 查看。",
                            Risk = "Warn"
                        });
                    }
                }
            }
        }
        catch { }
    }

    private static void CollectProxy(List<NetworkHealthItem> items)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return;

            int proxyEnable = key.GetValue("ProxyEnable") is int v ? v : 0;
            string? proxyServer = key.GetValue("ProxyServer") as string;
            string? proxyOverride = key.GetValue("ProxyOverride") as string;
            string? autoConfigUrl = key.GetValue("AutoConfigURL") as string;

            // WinHTTP proxy — netsh 输出系统区域编码(OEM)，直接 Run 用 UTF-8 读会乱码，
            // 改用 RunPowerShell 让 PS 把子进程输出重新编码为 UTF-8
            string? winHttpProxy = null;
            try
            {
                string output = ProcessRunner.RunPowerShell("netsh winhttp show proxy", out _, 5000);
                if (!string.IsNullOrWhiteSpace(output))
                    winHttpProxy = output.Trim();
            }
            catch { }

            if (proxyEnable == 1 && !string.IsNullOrWhiteSpace(proxyServer))
            {
                items.Add(new NetworkHealthItem
                {
                    Category = "代理",
                    Name = "系统代理 (IE/WinINET)",
                    Status = "已启用",
                    Detail = $"服务器：{proxyServer}" + (string.IsNullOrWhiteSpace(proxyOverride) ? "" : $"  |  例外：{proxyOverride}"),
                    Risk = "Warn"
                });
            }
            else
            {
                items.Add(new NetworkHealthItem
                {
                    Category = "代理",
                    Name = "系统代理 (IE/WinINET)",
                    Status = "未启用",
                    Detail = "ProxyEnable = 0。",
                    Risk = "None"
                });
            }

            if (!string.IsNullOrWhiteSpace(autoConfigUrl))
            {
                items.Add(new NetworkHealthItem
                {
                    Category = "代理",
                    Name = "PAC 自动配置",
                    Status = "已设置",
                    Detail = $"AutoConfigURL = {autoConfigUrl}",
                    Risk = "Info"
                });
            }

            if (!string.IsNullOrWhiteSpace(winHttpProxy))
            {
                // 多语言匹配：中文"直接访问"/"直接连接"，英文"Direct access"/"no proxy"
                bool isDirect = winHttpProxy.Contains("直接访问", StringComparison.OrdinalIgnoreCase)
                             || winHttpProxy.Contains("直接连接", StringComparison.OrdinalIgnoreCase)
                             || winHttpProxy.Contains("Direct access", StringComparison.OrdinalIgnoreCase)
                             || winHttpProxy.Contains("no proxy", StringComparison.OrdinalIgnoreCase);
                // 只在实际设置了代理时才报告，直接连接不算风险
                if (!isDirect)
                {
                    // 提取代理服务器地址，不展示原始 netsh 输出。避免匹配到包含冒号的标题行。
                    string? line = winHttpProxy.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(l => (l.Contains("Proxy Server", StringComparison.OrdinalIgnoreCase)
                                           || l.Contains("代理服务器"))
                                          && !l.Contains("设置", StringComparison.OrdinalIgnoreCase)
                                          && !l.Contains("settings", StringComparison.OrdinalIgnoreCase));

                    string proxyAddr = winHttpProxy;
                    if (line != null)
                    {
                        int colonIdx = line.IndexOf(':');
                        if (colonIdx >= 0)
                        {
                            proxyAddr = line.Substring(colonIdx + 1).Trim();
                        }
                    }

                    items.Add(new NetworkHealthItem
                    {
                        Category = "代理",
                        Name = "WinHTTP 代理",
                        Status = "已设置代理",
                        Detail = proxyAddr.Length > 120 ? proxyAddr[..120] + "..." : proxyAddr,
                        Risk = "Warn"
                    });
                }
            }
        }
        catch { }
    }

    private static void CollectVpn(List<NetworkHealthItem> items)
    {
        // Rasdial VPN connections
        try
        {
            string output = ProcessRunner.RunPowerShell(
                "Get-VpnConnection -ErrorAction SilentlyContinue | Select-Object Name, SplitTunneling, ConnectionStatus | ConvertTo-Csv -NoTypeInformation",
                out _, 10000);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                string[] headers = ParseCsvLine(lines[0]);
                int idxName = Array.IndexOf(headers, "Name");
                int idxSplit = Array.IndexOf(headers, "SplitTunneling");
                int idxStatus = Array.IndexOf(headers, "ConnectionStatus");

                for (int i = 1; i < lines.Length; i++)
                {
                    string[] values = ParseCsvLine(lines[i]);
                    if (values.Length < 1) continue;
                    string name = idxName >= 0 && idxName < values.Length ? values[idxName] : "";
                    string split = idxSplit >= 0 && idxSplit < values.Length ? values[idxSplit] : "";
                    string status = idxStatus >= 0 && idxStatus < values.Length ? values[idxStatus] : "";

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    bool connected = status.Contains("Connected", StringComparison.OrdinalIgnoreCase)
                                  || status.Contains("已连接", StringComparison.OrdinalIgnoreCase);

                    items.Add(new NetworkHealthItem
                    {
                        Category = "VPN",
                        Name = name,
                        Status = connected ? "已连接" : "已配置(未连接)",
                        Detail = $"SplitTunneling={split}",
                        Risk = connected ? "Warn" : "Info"
                    });
                }
            }
        }
        catch { }
    }

    private static void CollectFirewall(List<NetworkHealthItem> items)
    {
        // 用 Get-NetFirewallProfile 替代 netsh advfirewall，避免区域设置导致的输出格式差异
        try
        {
            string output = ProcessRunner.RunPowerShell(
                "Get-NetFirewallProfile -ErrorAction SilentlyContinue | " +
                "Select-Object Name, Enabled | ConvertTo-Csv -NoTypeInformation",
                out _, 8000);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return;

            string[] headers = ParseCsvLine(lines[0]);
            int idxName = Array.IndexOf(headers, "Name");
            int idxEnabled = Array.IndexOf(headers, "Enabled");

            var profiles = new List<(string name, bool enabled)>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] values = ParseCsvLine(lines[i]);
                if (values.Length < 2) continue;
                string name = idxName >= 0 && idxName < values.Length ? values[idxName] : "";
                string enabledStr = idxEnabled >= 0 && idxEnabled < values.Length ? values[idxEnabled] : "";
                bool enabled = enabledStr.Equals("True", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(name))
                    profiles.Add((name, enabled));
            }

            if (profiles.Count == 0) return;

            bool allOn = profiles.All(p => p.enabled);
            bool anyOff = profiles.Any(p => !p.enabled);
            string profileList = string.Join("、", profiles.Select(p => $"{p.name}({(p.enabled ? "开" : "关")})"));

            items.Add(new NetworkHealthItem
            {
                Category = "防火墙",
                Name = "Windows Defender 防火墙",
                Status = anyOff ? "部分配置文件已关闭" : "所有配置文件已启用",
                Detail = profileList,
                Risk = anyOff ? "Warn" : "None"
            });
        }
        catch { }
    }

    // 网络适配器信息已在其他标签页展示，此处不再重复

    private static void CollectServices(List<NetworkHealthItem> items)
    {
        // IP Helper - critical for IPv6/Teredo/ISATAP
        CheckService(items, "iphlpsvc", "IP Helper", "提供 IPv6 转换技术（6to4, ISATAP, Teredo, IP-HTTPS）。停止将禁用这些功能。");
        // DNS Client
        CheckService(items, "Dnscache", "DNS Client", "DNS 解析缓存服务。");
        // DHCP Client
        CheckService(items, "Dhcp", "DHCP Client", "DHCP 客户端，自动获取 IP 地址。");
        // SSDP Discovery (UPnP)
        CheckService(items, "SSDPSRV", "SSDP Discovery", "UPnP 设备发现。");
        // Function Discovery Resource Publication
        CheckService(items, "FDResPub", "Function Discovery", "网络设备发现和发布。");
    }

    private static void CollectDns(List<NetworkHealthItem> items)
    {
        // NRPT rules
        try
        {
            string output = ProcessRunner.RunPowerShell(
                "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Measure-Object | Select-Object -ExpandProperty Count",
                out _, 10000);
            if (int.TryParse(output.Trim(), out int count) && count > 0)
            {
                items.Add(new NetworkHealthItem
                {
                    Category = "DNS",
                    Name = "NRPT 规则",
                    Status = $"{count} 条",
                    Detail = "名称解析策略表规则可能导致特定域名解析到非预期的 DNS 服务器。",
                    Risk = "Info"
                });
            }
        }
        catch { }

        // DNS suffix search list
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            if (key != null)
            {
                string? suffix = key.GetValue("SearchList") as string;
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    items.Add(new NetworkHealthItem
                    {
                        Category = "DNS",
                        Name = "DNS 后缀搜索列表",
                        Status = suffix,
                        Detail = "自定义后缀搜索列表可能影响短域名解析。",
                        Risk = "Info"
                    });
                }
            }
        }
        catch { }
    }

    private static void CollectHosts(List<NetworkHealthItem> items)
    {
        try
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
            if (!File.Exists(hostsPath)) return;

            var lines = File.ReadAllLines(hostsPath);
            int activeRules = lines.Count(l =>
            {
                var t = l.Trim();
                return !string.IsNullOrEmpty(t) && !t.StartsWith("#");
            });

            if (activeRules > 0)
            {
                items.Add(new NetworkHealthItem
                {
                    Category = "Hosts",
                    Name = "Hosts 文件",
                    Status = $"{activeRules} 条有效规则",
                    Detail = activeRules > 10
                        ? $"规则数较多（{activeRules} 条），可能导致域名解析异常。可在「DNS」标签页的 Hosts 快捷按钮打开编辑。"
                        : "可在「DNS」标签页的 Hosts 快捷按钮打开编辑。",
                    Risk = activeRules > 10 ? "Warn" : "Info"
                });
            }
        }
        catch { }
    }

    // ------------------------------ Helpers ------------------------------

    private static void CheckService(List<NetworkHealthItem> items, string serviceName, string displayName, string description)
    {
        if (!ServiceExists(serviceName)) return;
        string st = ServiceRunning(serviceName) ? "运行中" : "已停止";
        items.Add(new NetworkHealthItem
        {
            Category = "系统服务",
            Name = displayName,
            Status = st,
            Detail = description,
            Risk = "None"
        });
    }

    /// <summary>
    /// 精确检查服务是否存在 — sc query 做模糊匹配会误报（如查 Wintun 匹配到 WintunHelper），
    /// 改用 Get-Service 做精确名称匹配。
    /// </summary>
    private static bool ServiceExists(string serviceName)
    {
        try
        {
            string safeName = ProcessRunner.EscapePsSingleQuoted(serviceName);
            string output = ProcessRunner.RunPowerShell(
                $"Get-Service -Name '{safeName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name",
                out _, 5000);
            return output.Trim().Equals(serviceName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ServiceRunning(string serviceName)
    {
        try
        {
            string safeName = ProcessRunner.EscapePsSingleQuoted(serviceName);
            string output = ProcessRunner.RunPowerShell(
                $"Get-Service -Name '{safeName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Status",
                out _, 5000);
            return output.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<string> GetAdapterNames()
    {
        var names = new List<string>();
        try
        {
            string output = ProcessRunner.RunPowerShell(
                "Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name",
                out _, 8000);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (!string.IsNullOrEmpty(t)) names.Add(t);
            }
        }
        catch { }
        return names;
    }

    private static bool AdapterNameContains(string keyword)
    {
        return GetAdapterNames().Any(n => n.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 通过 driverquery /SVC 精确确认驱动是否在内核中加载。
    /// /SVC 格式：显示名   类型   状态   服务名（最后一列是精确的驱动服务名）。
    /// 必须精确匹配最后一列，避免子串误报。
    /// </summary>
    private static bool IsDriverLoaded(string serviceName)
    {
        try
        {
            string output = ProcessRunner.Run("driverquery", "/FO CSV /SVC", out _, 8000);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("\"")) // 跳过表头
                {
                    var parts = ParseCsvLine(line);
                    if (parts.Length >= 4)
                    {
                        // CSV: "显示名","类型","状态","服务名"
                        string svcName = parts[3].Trim();
                        string state = parts[2].Trim();
                        if (svcName.Equals(serviceName, StringComparison.OrdinalIgnoreCase) &&
                            (state.Contains("Running", StringComparison.OrdinalIgnoreCase) || state.Contains("正在运行", StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
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
