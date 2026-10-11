using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetManager.Services;

public sealed class DiagnosticBundleOptions
{
    public bool IncludePacketCapture { get; set; }
    public int PacketCaptureSeconds { get; set; } = 10;
}

public sealed class DiagnosticBundleResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public string ZipPath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public int FileCount { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public string ErrorMessage { get; set; } = "";
}

internal sealed class DiagnosticStepRecord
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Status { get; set; } = "OK";
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
    public string Error { get; set; } = "";
}

internal sealed class PingProbeResult
{
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Sent { get; set; }
    public int Received { get; set; }
    public long MinMs { get; set; }
    public long MaxMs { get; set; }
    public double AvgMs { get; set; }
    public string ResolvedAddress { get; set; } = "";
    public string DnsStatus { get; set; } = "";
    public string Error { get; set; } = "";
    public double LossPercent => Sent <= 0 ? 100 : (Sent - Received) * 100.0 / Sent;
}

internal sealed class HttpProbeResult
{
    public string Url { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool ResponseReceived { get; set; }
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed class TrafficSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Speed { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public long UnicastReceived { get; set; }
    public long UnicastSent { get; set; }
    public long NonUnicastReceived { get; set; }
    public long NonUnicastSent { get; set; }
    public long IncomingErrors { get; set; }
    public long OutgoingErrors { get; set; }
    public long IncomingDiscarded { get; set; }
    public long OutgoingDiscarded { get; set; }
}

internal sealed class TrafficDelta
{
    public string Name { get; set; } = "";
    public double RxMbps { get; set; }
    public double TxMbps { get; set; }
    public double RxPacketsPerSecond { get; set; }
    public double TxPacketsPerSecond { get; set; }
    public double NonUnicastRxPerSecond { get; set; }
    public long ErrorDelta { get; set; }
    public long DiscardDelta { get; set; }
}

public static class DiagnosticBundleService
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static DiagnosticBundleResult Create(
        string zipPath,
        DiagnosticBundleOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DiagnosticBundleOptions();
        var started = DateTimeOffset.Now;
        string workDir = Path.Combine(
            Path.GetTempPath(),
            $"WinNetManager-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workDir);
        var records = new List<DiagnosticStepRecord>();
        var findings = new List<string>();
        var healthItems = new List<NetworkHealthItem>();
        var pingResults = new List<PingProbeResult>();
        var httpResults = new List<HttpProbeResult>();
        var trafficDeltas = new List<TrafficDelta>();
        int warningCount = 0;
        int errorCount = 0;
        bool zipCreated = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WritePackageReadme(workDir, options);

            progress?.Report("收集系统与网络健康快照...");
            try
            {
                healthItems = NetworkHealthService.GetSnapshot();
                WriteHealthSnapshot(workDir, healthItems);
                warningCount += healthItems.Count(i => i.Risk == "Warn" || i.Risk == "Danger");
            }
            catch (Exception ex)
            {
                WriteText(workDir, "00-summary/network-health-error.txt", ex.ToString());
                errorCount++;
            }

            var psSteps = BuildPowerShellSteps();
            foreach (var step in psSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(step.Name);
                var record = RunPowerShellStep(workDir, step.File, step.Name, step.Script, step.TimeoutMs, cancellationToken);
                records.Add(record);
                if (record.Status != "OK") errorCount++;
            }

            var nativeSteps = BuildNativeSteps();
            foreach (var step in nativeSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(step.Name);
                var record = RunNativeStep(workDir, step.File, step.Name, step.FileName, step.Arguments, step.TimeoutMs, cancellationToken);
                records.Add(record);
                if (record.Status != "OK") errorCount++;
            }

            progress?.Report("导出 Windows Filtering Platform (WFP) 状态...");
            foreach (var record in CollectWfpSnapshot(workDir, cancellationToken))
            {
                records.Add(record);
                if (record.Status == "WARN") errorCount++;
            }

            progress?.Report("采样网卡流量、丢包和错误计数（5 秒）...");
            try
            {
                trafficDeltas = CaptureTrafficSample(workDir, 5, cancellationToken);
                foreach (var d in trafficDeltas)
                {
                    if (d.NonUnicastRxPerSecond >= 20000)
                    {
                        findings.Add($"[危险] {d.Name} 非单播接收约 {d.NonUnicastRxPerSecond:N0} 包/秒，疑似广播/组播风暴。");
                        warningCount++;
                    }
                    else if (d.NonUnicastRxPerSecond >= 2000)
                    {
                        findings.Add($"[警告] {d.Name} 非单播接收约 {d.NonUnicastRxPerSecond:N0} 包/秒，广播/组播流量偏高。");
                        warningCount++;
                    }

                    if (d.ErrorDelta > 0 || d.DiscardDelta > 10)
                    {
                        findings.Add($"[警告] {d.Name} 采样期间错误 {d.ErrorDelta}、丢弃 {d.DiscardDelta}，需检查链路/驱动/拥塞。");
                        warningCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteText(workDir, "02-adapters/statistics-sample-error.txt", ex.ToString());
                errorCount++;
            }

            progress?.Report("测试网关、公共 IP、IPv4/IPv6 与常用域名...");
            try
            {
                pingResults = RunPingProbes(workDir, findings, ref warningCount, cancellationToken);
            }
            catch (Exception ex)
            {
                WriteText(workDir, "05-connectivity/ping-error.txt", ex.ToString());
                errorCount++;
            }

            progress?.Report("执行条件化 Traceroute（并发、限时）...");
            foreach (var record in RunConditionalTraceroutes(workDir, pingResults, cancellationToken))
            {
                records.Add(record);
                if (record.Status == "WARN") errorCount++;
            }

            progress?.Report("测试 HTTP / HTTPS（直连与系统代理路径）...");
            try
            {
                httpResults = RunHttpProbes(workDir, cancellationToken);
            }
            catch (Exception ex)
            {
                WriteText(workDir, "05-connectivity/http-error.txt", ex.ToString());
                errorCount++;
            }

            progress?.Report("检查近期 IP 冲突与网络事件...");
            try
            {
                int conflicts = GetRecentIpConflictCount(cancellationToken);
                if (conflicts > 0)
                {
                    findings.Add($"[危险] 最近 48 小时检测到 {conflicts} 条 TCP/IP 地址冲突事件（Event ID 4199）。");
                    warningCount++;
                }
            }
            catch { }

            AnalyzeConnectivity(findings, pingResults, httpResults, ref warningCount);
            AnalyzeTcpPortPressure(findings, ref warningCount);
            AddStructuralFindings(findings, ref warningCount);

            if (options.IncludePacketCapture)
            {
                progress?.Report($"附加 PktMon 抓包（{options.PacketCaptureSeconds} 秒）...");
                var captureRecord = CapturePktMon(workDir, Math.Clamp(options.PacketCaptureSeconds, 5, 30), cancellationToken);
                records.Add(captureRecord);
                if (captureRecord.Status == "WARN") errorCount++;
            }

            progress?.Report("生成 SUMMARY.md 与 manifest.json...");
            WriteSummary(workDir, started, findings, healthItems, pingResults, httpResults, trafficDeltas, records, options);
            WriteManifest(workDir, started, records, options);

            cancellationToken.ThrowIfCancellationRequested();
            string? parent = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            PreflightDiskSpace(workDir, zipPath, options.IncludePacketCapture);

            progress?.Report("压缩诊断包...");
            string partialZip = zipPath + $".partial-{Guid.NewGuid():N}";
            try
            {
                ZipFile.CreateFromDirectory(workDir, partialZip, CompressionLevel.Optimal, includeBaseDirectory: false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialZip, zipPath, overwrite: true);
                zipCreated = true;
            }
            finally
            {
                try
                {
                    if (File.Exists(partialZip))
                        File.Delete(partialZip);
                }
                catch { }
            }

            int fileCount = Directory.GetFiles(workDir, "*", SearchOption.AllDirectories).Length;
            return new DiagnosticBundleResult
            {
                Success = true,
                ZipPath = zipPath,
                WorkingDirectory = "",
                Duration = DateTimeOffset.Now - started,
                FileCount = fileCount,
                WarningCount = warningCount,
                ErrorCount = errorCount
            };
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticBundleResult
            {
                Success = false,
                Cancelled = true,
                WorkingDirectory = workDir,
                Duration = DateTimeOffset.Now - started,
                WarningCount = warningCount,
                ErrorCount = errorCount,
                ErrorMessage = "用户已取消深度诊断。"
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticBundleResult
            {
                Success = false,
                ZipPath = zipCreated ? zipPath : "",
                WorkingDirectory = workDir,
                Duration = DateTimeOffset.Now - started,
                WarningCount = warningCount,
                ErrorCount = errorCount + 1,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (zipCreated)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }
    }

    private static List<(string File, string Name, string Script, int TimeoutMs)> BuildPowerShellSteps()
    {
        return new()
        {
            ("01-system/os-computer.txt", "收集 Windows / 硬件 / 启动时间", """
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime,LocalDateTime
$cs = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,Domain,PartOfDomain,TotalPhysicalMemory
$bios = Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion,ReleaseDate
"=== Operating System ==="
$os | Format-List * | Out-String -Width 4096
"=== Computer System ==="
$cs | Format-List * | Out-String -Width 4096
"=== BIOS ==="
$bios | Format-List * | Out-String -Width 4096
""", 15000),

            ("01-system/hotfixes.txt", "收集最近 Windows 更新", """
Get-HotFix -ErrorAction SilentlyContinue |
  Sort-Object InstalledOn -Descending |
  Select-Object -First 40 HotFixID,Description,InstalledOn |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("02-adapters/net-adapters.txt", "收集全部网卡、驱动与链路状态", """
Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
  Select-Object Name,InterfaceDescription,ifIndex,Status,MediaConnectionState,LinkSpeed,MacAddress,
                DriverInformation,DriverFileName,DriverVersion,NdisVersion,Virtual,VlanID |
  Sort-Object ifIndex |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("02-adapters/ip-configuration.txt", "收集 IP / DHCP / 网关配置", """
Get-NetIPConfiguration -All -Detailed -ErrorAction SilentlyContinue |
  Format-List * | Out-String -Width 4096
""", 20000),

            ("02-adapters/ip-addresses.txt", "收集全部 IPv4 / IPv6 地址", """
Get-NetIPAddress -IncludeAllCompartments -ErrorAction SilentlyContinue |
  Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,IPAddress,PrefixLength,PrefixOrigin,SuffixOrigin,
                AddressState,ValidLifetime,PreferredLifetime,SkipAsSource,PolicyStore |
  Sort-Object InterfaceIndex,AddressFamily,IPAddress |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("02-adapters/ip-interfaces.txt", "收集接口跃点、MTU 与转发状态", """
Get-NetIPInterface -IncludeAllCompartments -ErrorAction SilentlyContinue |
  Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,ConnectionState,Dhcp,RouterDiscovery,
                InterfaceMetric,NlMtu,Forwarding,WeakHostSend,WeakHostReceive,IgnoreDefaultRoutes,AutomaticMetric |
  Sort-Object InterfaceIndex,AddressFamily |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("02-adapters/bindings.txt", "收集网卡协议与过滤器绑定", """
Get-NetAdapterBinding -IncludeHidden -AllBindings -ErrorAction SilentlyContinue |
  Select-Object Name,DisplayName,ComponentID,Enabled |
  Sort-Object Name,ComponentID |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("02-adapters/advanced-properties.txt", "收集网卡高级属性", """
Get-NetAdapterAdvancedProperty -IncludeHidden -AllProperties -ErrorAction SilentlyContinue |
  Select-Object Name,DisplayName,DisplayValue,RegistryKeyword,RegistryValue |
  Sort-Object Name,DisplayName |
  Format-Table -AutoSize | Out-String -Width 4096
""", 25000),

            ("02-adapters/statistics-cumulative.txt", "收集网卡累计统计计数", """
Get-NetAdapterStatistics -IncludeHidden -ErrorAction SilentlyContinue |
  Format-List * | Out-String -Width 4096
""", 15000),

            ("02-adapters/power-management.txt", "收集网卡电源管理设置", """
Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue |
  Format-List * | Out-String -Width 4096
""", 15000),

            ("02-adapters/tcp-offload-settings.txt", "收集 TCP / RSS / Offload 全局设置", """
"=== TCP settings ==="
Get-NetTCPSetting -ErrorAction SilentlyContinue |
  Select-Object SettingName,CongestionProvider,AutoTuningLevelLocal,EcnCapability,ScalingHeuristics,InitialRto,MinRto |
  Format-Table -AutoSize | Out-String -Width 4096
"=== Offload global settings ==="
Get-NetOffloadGlobalSetting -ErrorAction SilentlyContinue |
  Format-List * | Out-String -Width 4096
"=== RSS ==="
Get-NetAdapterRss -ErrorAction SilentlyContinue |
  Select-Object Name,Enabled,NumberOfReceiveQueues,Profile,BaseProcessorNumber,MaxProcessorNumber |
  Format-Table -AutoSize | Out-String -Width 4096
"=== LSO ==="
if (Get-Command Get-NetAdapterLso -ErrorAction SilentlyContinue) {
  Get-NetAdapterLso -ErrorAction SilentlyContinue |
    Select-Object Name,IPv4Enabled,IPv6Enabled | Format-Table -AutoSize | Out-String -Width 4096
}
"=== Checksum Offload ==="
if (Get-Command Get-NetAdapterChecksumOffload -ErrorAction SilentlyContinue) {
  Get-NetAdapterChecksumOffload -ErrorAction SilentlyContinue |
    Format-Table -AutoSize | Out-String -Width 4096
}
"=== RSC ==="
if (Get-Command Get-NetAdapterRsc -ErrorAction SilentlyContinue) {
  Get-NetAdapterRsc -ErrorAction SilentlyContinue |
    Format-Table -AutoSize | Out-String -Width 4096
}
""", 20000),

            ("03-routing/routes-active.txt", "收集活动路由表", """
Get-NetRoute -IncludeAllCompartments -ErrorAction SilentlyContinue |
  Select-Object AddressFamily,DestinationPrefix,NextHop,RouteMetric,InterfaceMetric,ifIndex,InterfaceAlias,
                State,Protocol,Publish,Store |
  Sort-Object AddressFamily,DestinationPrefix,RouteMetric |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("03-routing/routes-persistent.txt", "收集持久路由", """
Get-NetRoute -PolicyStore PersistentStore -ErrorAction SilentlyContinue |
  Select-Object AddressFamily,DestinationPrefix,NextHop,RouteMetric,ifIndex,InterfaceAlias,Protocol |
  Sort-Object AddressFamily,DestinationPrefix,RouteMetric |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("03-routing/neighbors.txt", "收集 ARP / IPv6 邻居缓存", """
Get-NetNeighbor -IncludeAllCompartments -ErrorAction SilentlyContinue |
  Select-Object ifIndex,InterfaceAlias,IPAddress,LinkLayerAddress,State,AddressFamily |
  Sort-Object ifIndex,IPAddress |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("02-adapters/dhcp-registry.txt", "收集 DHCP 租约与接口注册表状态", """
$base = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
  $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
  $leaseObtained = if ($p.LeaseObtainedTime) {
    try { [DateTimeOffset]::FromUnixTimeSeconds([int64]$p.LeaseObtainedTime).LocalDateTime } catch { $null }
  } else { $null }
  $leaseEnds = if ($p.LeaseTerminatesTime) {
    try { [DateTimeOffset]::FromUnixTimeSeconds([int64]$p.LeaseTerminatesTime).LocalDateTime } catch { $null }
  } else { $null }
  [pscustomobject]@{
    InterfaceGuid = $_.PSChildName
    EnableDHCP = $p.EnableDHCP
    DhcpIPAddress = $p.DhcpIPAddress
    DhcpSubnetMask = $p.DhcpSubnetMask
    DhcpDefaultGateway = ($p.DhcpDefaultGateway -join ',')
    DhcpServer = $p.DhcpServer
    DhcpNameServer = $p.DhcpNameServer
    DhcpDomain = $p.DhcpDomain
    LeaseObtainedTime = $p.LeaseObtainedTime
    LeaseObtainedLocal = $leaseObtained
    LeaseTerminatesTime = $p.LeaseTerminatesTime
    LeaseTerminatesLocal = $leaseEnds
  }
} | Format-List * | Out-String -Width 4096
""", 15000),

            ("04-dns/dns-client.txt", "收集 DNS 服务器、后缀和客户端设置", """
"=== DNS servers ==="
Get-DnsClientServerAddress -ErrorAction SilentlyContinue |
  Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,ServerAddresses |
  Format-Table -AutoSize | Out-String -Width 4096
"=== DNS client ==="
Get-DnsClient -ErrorAction SilentlyContinue |
  Select-Object InterfaceAlias,InterfaceIndex,ConnectionSpecificSuffix,RegisterThisConnectionsAddress,
                UseSuffixWhenRegistering |
  Format-Table -AutoSize | Out-String -Width 4096
"=== NRPT ==="
Get-DnsClientNrptRule -ErrorAction SilentlyContinue |
  Select-Object Name,Namespace,NameServers,Comment,GpoName |
  Format-Table -AutoSize | Out-String -Width 4096
"=== DoH servers (if supported) ==="
if (Get-Command Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {
  Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue |
    Format-Table -AutoSize | Out-String -Width 4096
}
""", 20000),

            ("04-dns/hosts.txt", "收集 Hosts 文件", """
$path = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
if (Test-Path $path) { Get-Content $path -ErrorAction SilentlyContinue } else { 'Hosts file not found.' }
""", 10000),

            ("04-dns/resolve-tests.txt", "测试系统 DNS 与每个已配置 DNS 服务器", """
$names = @('www.baidu.com','www.qq.com','www.microsoft.com','github.com')
"=== System resolver ==="
foreach ($name in $names) {
  "----- $name -----"
  try {
    Resolve-DnsName $name -DnsOnly -QuickTimeout -ErrorAction Stop |
      Select-Object Name,Type,IPAddress,NameHost |
      Format-Table -AutoSize | Out-String -Width 4096
  } catch {
    "ERROR: $($_.Exception.Message)"
  }
}
"=== Per configured DNS server ==="
$servers = Get-DnsClientServerAddress -ErrorAction SilentlyContinue |
  ForEach-Object { $_.ServerAddresses } |
  Where-Object { $_ } |
  Sort-Object -Unique
foreach ($server in $servers) {
  "===== SERVER $server ====="
  foreach ($name in @('www.baidu.com','www.microsoft.com')) {
    "----- $name -----"
    try {
      Resolve-DnsName $name -Server $server -DnsOnly -QuickTimeout -ErrorAction Stop |
        Select-Object Name,Type,IPAddress,NameHost |
        Format-Table -AutoSize | Out-String -Width 4096
    } catch {
      "ERROR: $($_.Exception.Message)"
    }
  }
}
""", 45000),

            ("05-connectivity/connection-profiles.txt", "收集 Windows 网络位置与 NLA 状态", """
Get-NetConnectionProfile -ErrorAction SilentlyContinue |
  Select-Object Name,InterfaceAlias,InterfaceIndex,NetworkCategory,IPv4Connectivity,IPv6Connectivity |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("05-connectivity/ncsi-settings.txt", "收集 Windows NCSI 联网探测设置", """
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet' -ErrorAction SilentlyContinue |
  Select-Object EnableActiveProbing,ActiveDnsProbeContent,ActiveDnsProbeHost,ActiveWebProbeContent,
                ActiveWebProbeHost,ActiveWebProbePath |
  Format-List * | Out-String -Width 4096
""", 10000),

            ("03-routing/nat-compartments.txt", "收集 NAT 与网络隔离 compartment", """
"=== NetNat ==="
Get-NetNat -ErrorAction SilentlyContinue | Format-List * | Out-String -Width 4096
"=== NetCompartment ==="
Get-NetCompartment -ErrorAction SilentlyContinue |
  Select-Object CompartmentId,CompartmentDescription,CompartmentType,Status |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("06-proxy-vpn/wininet-proxy.txt", "收集 WinINET / PAC / GPO 代理设置", """
"=== Current user Internet Settings ==="
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue |
  Select-Object ProxyEnable,ProxyServer,ProxyOverride,AutoConfigURL,AutoDetect |
  Format-List * | Out-String -Width 4096
"=== Machine Internet Settings ==="
Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue |
  Select-Object ProxyEnable,ProxyServer,ProxyOverride,AutoConfigURL,AutoDetect |
  Format-List * | Out-String -Width 4096
"=== Internet Settings policy ==="
Get-ItemProperty 'HKLM:\Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue |
  Select-Object ProxySettingsPerUser,ProxySettingsPerUserPolicy |
  Format-List * | Out-String -Width 4096
"=== Current user Connections blobs ==="
$p = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections' -ErrorAction SilentlyContinue
foreach ($n in @('DefaultConnectionSettings','SavedLegacySettings')) {
  $v = $p.$n
  if ($v) {
    [pscustomobject]@{
      Name = $n
      Length = $v.Length
      Hex = (($v | ForEach-Object { $_.ToString('X2') }) -join '')
    } | Format-List * | Out-String -Width 4096
  }
}
""", 12000),

            ("06-proxy-vpn/vpn.txt", "收集 Windows VPN 配置与状态", """
"=== Current-user VPN ==="
Get-VpnConnection -ErrorAction SilentlyContinue |
  Select-Object Name,ServerAddress,TunnelType,SplitTunneling,ConnectionStatus,RememberCredential |
  Format-Table -AutoSize | Out-String -Width 4096
"=== All-user VPN ==="
Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue |
  Select-Object Name,ServerAddress,TunnelType,SplitTunneling,ConnectionStatus,RememberCredential |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("07-firewall/profiles.txt", "收集 Windows 防火墙配置文件", """
Get-NetFirewallProfile -ErrorAction SilentlyContinue |
  Select-Object Name,Enabled,DefaultInboundAction,DefaultOutboundAction,AllowInboundRules,
                AllowLocalFirewallRules,NotifyOnListen,LogFileName,LogMaxSizeKilobytes,LogBlocked,LogAllowed |
  Format-List * | Out-String -Width 4096
""", 15000),

            ("07-firewall/enabled-rules.csv", "导出全部已启用防火墙规则", """
Get-NetFirewallRule -Enabled True -ErrorAction SilentlyContinue |
  Select-Object DisplayName,Name,Direction,Action,Profile,InterfaceType,EdgeTraversalPolicy,
                PolicyStoreSourceType,PolicyStoreSource |
  ConvertTo-Csv -NoTypeInformation
""", 30000),

            ("07-firewall/block-rules-detail.txt", "收集启用的阻止规则及端口/地址过滤器", """
$rules = Get-NetFirewallRule -Enabled True -Action Block -ErrorAction SilentlyContinue
foreach ($r in $rules) {
  "===== $($r.DisplayName) ====="
  $r | Select-Object Name,DisplayName,Direction,Action,Profile,InterfaceType,PolicyStoreSource | Format-List | Out-String -Width 4096
  $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue | Format-List * | Out-String -Width 4096
  $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Format-List * | Out-String -Width 4096
}
""", 30000),

            ("06-proxy-vpn/network-provider-order.txt", "收集 Network Provider 顺序", """
"=== Provider Order ==="
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\NetworkProvider\Order' -ErrorAction SilentlyContinue |
  Select-Object ProviderOrder | Format-List * | Out-String -Width 4096
"=== Hardware Provider Order ==="
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\NetworkProvider\HWOrder' -ErrorAction SilentlyContinue |
  Select-Object ProviderOrder | Format-List * | Out-String -Width 4096
""", 10000),

            ("08-services/services.txt", "收集关键网络服务", """
$names = @('Dhcp','Dnscache','NlaSvc','netprofm','WlanSvc','dot3svc','EapHost','WwanSvc','Winmgmt','EventLog',
           'iphlpsvc','WinHttpAutoProxySvc','BFE','MpsSvc','PolicyAgent','IKEEXT','RemoteAccess','RasMan','lmhosts','SharedAccess')
Get-Service -ErrorAction SilentlyContinue |
  Where-Object { $names -contains $_.Name -or $_.Name -match 'tailscale|zerotier|wireguard|proxifier|adguard|netlimiter' } |
  Select-Object Name,DisplayName,Status,StartType |
  Sort-Object Name |
  Format-Table -AutoSize | Out-String -Width 4096
""", 15000),

            ("08-services/network-drivers.txt", "收集网络相关系统驱动", """
Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
  Where-Object {
    $_.Name -match 'ndis|wfp|tap|tun|wire|tail|zero|prox|divert|npcap|npf|filter|adguard|netlimiter' -or
    $_.PathName -match 'ndis|wfp|tap|tun|wire|tail|zero|prox|divert|npcap|npf|filter|adguard|netlimiter'
  } |
  Select-Object Name,DisplayName,State,StartMode,PathName |
  Sort-Object Name |
  Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("09-connections/tcp-state-summary.txt", "统计 TCP 状态与高端口占用", """
$c = Get-NetTCPConnection -ErrorAction SilentlyContinue
"=== State counts ==="
$c | Group-Object State | Sort-Object Count -Descending |
  Select-Object Name,Count | Format-Table -AutoSize | Out-String -Width 4096
"=== High local ports (>=49152) ==="
$high = $c | Where-Object { $_.LocalPort -ge 49152 }
[pscustomobject]@{
  Connections = @($high).Count
  UniqueLocalPorts = @($high.LocalPort | Sort-Object -Unique).Count
  TimeWait = @($c | Where-Object State -eq 'TimeWait').Count
  CloseWait = @($c | Where-Object State -eq 'CloseWait').Count
  SynSent = @($c | Where-Object State -eq 'SynSent').Count
} | Format-List * | Out-String -Width 4096
"=== Top owning processes ==="
$c | Group-Object OwningProcess | Sort-Object Count -Descending | Select-Object -First 30 |
  ForEach-Object {
    $ownerPid = [int]$_.Name
    $name = try { (Get-Process -Id $ownerPid -ErrorAction Stop).ProcessName } catch { '' }
    [pscustomobject]@{ PID=$ownerPid; ProcessName=$name; Connections=$_.Count }
  } | Format-Table -AutoSize | Out-String -Width 4096
""", 20000),

            ("09-connections/tcp-connections.csv", "收集全部 TCP 连接", """
$procs = @{}
Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $procs[$_.Id] = $_.ProcessName }
Get-NetTCPConnection -ErrorAction SilentlyContinue |
  Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess,
    @{n='ProcessName';e={ $procs[[int]$_.OwningProcess] }} |
  ConvertTo-Csv -NoTypeInformation
""", 25000),

            ("09-connections/udp-endpoints.csv", "收集全部 UDP 端点", """
$procs = @{}
Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $procs[$_.Id] = $_.ProcessName }
Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
  Select-Object LocalAddress,LocalPort,OwningProcess,
    @{n='ProcessName';e={ $procs[[int]$_.OwningProcess] }} |
  ConvertTo-Csv -NoTypeInformation
""", 25000),

            ("10-events/network-events-48h.txt", "收集最近 48 小时网络相关系统事件", """
$start = (Get-Date).AddHours(-48)
$criticalIds = @(1014,4199,4201,1001,1002,50036,50058,50067,10400,10401,10402,10403)
$critical = Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$start;Id=$criticalIds} -ErrorAction SilentlyContinue

# Supplement with recent network-provider events. Critical IDs above are already safe from MaxEvents truncation.
$recent = Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$start} -MaxEvents 5000 -ErrorAction SilentlyContinue |
  Where-Object {
    $_.ProviderName -match 'Tcpip|Dhcp|DNS|NlaSvc|NetworkProfile|WLAN|NDIS|Netwtw|e2f|e2fn|Realtek|rt640x|Kernel-PnP|VmSwitch|EapHost|Wired-AutoConfig'
  }

@($critical + $recent) |
  Sort-Object RecordId -Unique |
  Sort-Object TimeCreated -Descending |
  Select-Object -First 1500 TimeCreated,Id,LevelDisplayName,ProviderName,Message |
  Format-List * | Out-String -Width 4096
""", 30000),

            ("10-events/dhcp-client-events-48h.txt", "收集最近 48 小时 DHCP Client 事件", """
$start = (Get-Date).AddHours(-48)
$logs = Get-WinEvent -ListLog '*Dhcp*' -ErrorAction SilentlyContinue |
  Where-Object {
    $_.LogName -match 'Dhcp-Client' -or
    $_.LogName -match 'DHCP Client Events'
  }
"=== Available DHCP client logs ==="
$logs | Select-Object LogName,IsEnabled,RecordCount,LastWriteTime |
  Format-Table -AutoSize | Out-String -Width 4096
foreach ($log in $logs) {
  "===== $($log.LogName) ====="
  if (-not $log.IsEnabled) {
    "Log is disabled."
    continue
  }
  try {
    Get-WinEvent -FilterHashtable @{LogName=$log.LogName;StartTime=$start} -MaxEvents 800 -ErrorAction Stop |
      Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message |
      Format-List * | Out-String -Width 4096
  } catch {
    "ERROR: $($_.Exception.Message)"
  }
}
""", 25000),

            ("10-events/dns-client-events-48h.txt", "收集最近 48 小时 DNS Client 事件", """
$start = (Get-Date).AddHours(-48)
$logs = @('Microsoft-Windows-DNS-Client/Operational','System')
foreach ($logName in $logs) {
  $log = Get-WinEvent -ListLog $logName -ErrorAction SilentlyContinue
  if (-not $log) { continue }
  "===== $logName ====="
  if (-not $log.IsEnabled) {
    "Log is disabled."
    continue
  }
  if ($logName -eq 'System') {
    Get-WinEvent -FilterHashtable @{LogName=$logName;ProviderName='Microsoft-Windows-DNS-Client';StartTime=$start} -MaxEvents 500 -ErrorAction SilentlyContinue |
      Select-Object TimeCreated,Id,LevelDisplayName,Message | Format-List * | Out-String -Width 4096
  } else {
    Get-WinEvent -FilterHashtable @{LogName=$logName;StartTime=$start} -MaxEvents 500 -ErrorAction SilentlyContinue |
      Select-Object TimeCreated,Id,LevelDisplayName,Message | Format-List * | Out-String -Width 4096
  }
}
""", 20000),

            ("10-events/enterprise-network-events-48h.txt", "收集 802.1X / EAP / Hyper-V vSwitch 事件", """
$start = (Get-Date).AddHours(-48)
$logs = @(
  'Microsoft-Windows-Wired-AutoConfig/Operational',
  'Microsoft-Windows-EapHost/Operational',
  'Microsoft-Windows-Hyper-V-VmSwitch/Operational'
)
foreach ($logName in $logs) {
  $log = Get-WinEvent -ListLog $logName -ErrorAction SilentlyContinue
  "===== $logName ====="
  if (-not $log) {
    "Log not present."
    continue
  }
  if (-not $log.IsEnabled) {
    "Log is disabled."
    continue
  }
  Get-WinEvent -FilterHashtable @{LogName=$logName;StartTime=$start} -MaxEvents 800 -ErrorAction SilentlyContinue |
    Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message |
    Format-List * | Out-String -Width 4096
}
""", 25000),

            ("10-events/wlan-events-48h.txt", "收集最近 48 小时 Wi-Fi 连接事件", """
$start = (Get-Date).AddHours(-48)
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-WLAN-AutoConfig/Operational';StartTime=$start} -ErrorAction SilentlyContinue |
  Select-Object -First 800 TimeCreated,Id,LevelDisplayName,Message |
  Format-List * | Out-String -Width 4096
""", 20000),

            ("10-events/network-profile-events-48h.txt", "收集最近 48 小时网络位置变化事件", """
$start = (Get-Date).AddHours(-48)
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-NetworkProfile/Operational';StartTime=$start} -ErrorAction SilentlyContinue |
  Select-Object -First 800 TimeCreated,Id,LevelDisplayName,Message |
  Format-List * | Out-String -Width 4096
""", 20000)
        };
    }

    private static List<(string File, string Name, string FileName, string Arguments, int TimeoutMs)> BuildNativeSteps()
    {
        return new()
        {
            ("02-adapters/ipconfig-all.txt", "运行 ipconfig /all", "ipconfig.exe", "/all", 15000),
            ("03-routing/route-print.txt", "运行 route print", "route.exe", "print", 15000),
            ("03-routing/arp-a.txt", "运行 arp -a", "arp.exe", "-a", 10000),
            ("02-adapters/netsh-ipv4-config.txt", "收集 netsh IPv4 配置", "netsh.exe", "interface ipv4 show config", 15000),
            ("02-adapters/netsh-ipv6-interfaces.txt", "收集 netsh IPv6 接口", "netsh.exe", "interface ipv6 show interfaces", 15000),
            ("03-routing/netsh-ipv6-routes.txt", "收集 netsh IPv6 路由", "netsh.exe", "interface ipv6 show route", 15000),
            ("02-adapters/wlan-interfaces.txt", "收集当前 Wi-Fi 连接", "netsh.exe", "wlan show interfaces", 15000),
            ("02-adapters/wlan-drivers.txt", "收集 Wi-Fi 驱动能力", "netsh.exe", "wlan show drivers", 15000),
            ("02-adapters/wlan-settings.txt", "收集 Wi-Fi 全局设置", "netsh.exe", "wlan show settings", 10000),
            ("02-adapters/wlan-profiles.txt", "列出 Wi-Fi 配置文件（不含密钥）", "netsh.exe", "wlan show profiles", 10000),
            ("02-adapters/lan-interfaces.txt", "收集有线 802.1X 接口状态", "netsh.exe", "lan show interfaces", 10000),
            ("02-adapters/lan-settings.txt", "收集有线 802.1X 全局设置", "netsh.exe", "lan show settings", 10000),
            ("02-adapters/lan-profiles.txt", "列出有线 802.1X 配置文件", "netsh.exe", "lan show profiles", 10000),
            ("02-adapters/mbn-interfaces.txt", "收集蜂窝/WWAN 接口", "netsh.exe", "mbn show interfaces", 10000),
            ("02-adapters/tcp-global.txt", "收集 netsh TCP 全局设置", "netsh.exe", "interface tcp show global", 15000),
            ("02-adapters/ipv6-prefix-policies.txt", "收集 IPv6 前缀优先级策略", "netsh.exe", "interface ipv6 show prefixpolicies", 15000),
            ("02-adapters/teredo-state.txt", "收集 Teredo 状态", "netsh.exe", "interface teredo show state", 10000),
            ("06-proxy-vpn/winhttp-proxy.txt", "收集 WinHTTP 代理", "netsh.exe", "winhttp show proxy", 10000),
            ("06-proxy-vpn/portproxy.txt", "收集 portproxy 转发规则", "netsh.exe", "interface portproxy show all", 10000),
            ("09-connections/dynamic-ports-ipv4-tcp.txt", "收集 IPv4 TCP 动态端口范围", "netsh.exe", "interface ipv4 show dynamicportrange protocol=tcp", 10000),
            ("09-connections/dynamic-ports-ipv4-udp.txt", "收集 IPv4 UDP 动态端口范围", "netsh.exe", "interface ipv4 show dynamicportrange protocol=udp", 10000),
            ("09-connections/dynamic-ports-ipv6-tcp.txt", "收集 IPv6 TCP 动态端口范围", "netsh.exe", "interface ipv6 show dynamicportrange protocol=tcp", 10000),
            ("09-connections/dynamic-ports-ipv6-udp.txt", "收集 IPv6 UDP 动态端口范围", "netsh.exe", "interface ipv6 show dynamicportrange protocol=udp", 10000),
            ("09-connections/excluded-ports-ipv4-tcp.txt", "收集 IPv4 TCP 排除端口范围", "netsh.exe", "interface ipv4 show excludedportrange protocol=tcp", 10000),
            ("09-connections/excluded-ports-ipv4-udp.txt", "收集 IPv4 UDP 排除端口范围", "netsh.exe", "interface ipv4 show excludedportrange protocol=udp", 10000),
            ("09-connections/excluded-ports-ipv6-tcp.txt", "收集 IPv6 TCP 排除端口范围", "netsh.exe", "interface ipv6 show excludedportrange protocol=tcp", 10000),
            ("09-connections/excluded-ports-ipv6-udp.txt", "收集 IPv6 UDP 排除端口范围", "netsh.exe", "interface ipv6 show excludedportrange protocol=udp", 10000),
            ("02-adapters/dhcp-client-trace-dump.txt", "导出 DHCP Client 最近跟踪记录", "netsh.exe", "dhcpclient trace dump", 15000),
            ("08-services/winsock-catalog.txt", "收集 Winsock Catalog", "netsh.exe", "winsock show catalog", 20000),
            ("08-services/driverquery.csv", "导出驱动列表", "driverquery.exe", "/FO CSV", 15000),
            ("09-connections/netstat-ano.txt", "运行 netstat -ano", "netstat.exe", "-ano", 20000),
            ("01-system/time-sync.txt", "检查 Windows 时间同步", "w32tm.exe", "/query /status", 10000)
        };
    }

    private static DiagnosticStepRecord RunPowerShellStep(
        string root, string relative, string name, string script, int timeoutMs, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string output = ProcessRunner.RunPowerShell(script, out string error, out int exitCode, timeoutMs, cancellationToken);
        sw.Stop();
        string status = exitCode == 0 && string.IsNullOrWhiteSpace(error) ? "OK" : "WARN";

        WriteCommandResult(root, relative, name, "PowerShell", output, error, exitCode, sw.Elapsed);
        return new DiagnosticStepRecord
        {
            Name = name,
            File = relative,
            Status = status,
            ExitCode = exitCode,
            DurationMs = sw.ElapsedMilliseconds,
            Error = error
        };
    }

    private static DiagnosticStepRecord RunNativeStep(
        string root, string relative, string name, string fileName, string arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string output = ProcessRunner.RunNative(fileName, arguments, out string error, out int exitCode, timeoutMs, cancellationToken);
        sw.Stop();
        string status = exitCode == 0 && string.IsNullOrWhiteSpace(error) ? "OK" : "WARN";

        WriteCommandResult(root, relative, name, $"{fileName} {arguments}", output, error, exitCode, sw.Elapsed);
        return new DiagnosticStepRecord
        {
            Name = name,
            File = relative,
            Status = status,
            ExitCode = exitCode,
            DurationMs = sw.ElapsedMilliseconds,
            Error = error
        };
    }

    private static List<DiagnosticStepRecord> CollectWfpSnapshot(string root, CancellationToken cancellationToken)
    {
        EnsureDirectory(root, "07-firewall");

        if (!IsAdministrator())
        {
            const string message = "当前进程不是管理员权限，跳过 WFP state/filters 导出。";
            WriteText(root, "07-firewall/wfp-skipped.txt", message);
            return new List<DiagnosticStepRecord>
            {
                new()
                {
                    Name = "导出 WFP 状态",
                    File = "07-firewall/wfp-skipped.txt",
                    Status = "SKIP",
                    ExitCode = 0,
                    Error = message
                }
            };
        }

        var jobs = new[]
        {
            (Name: "WFP state", Relative: "07-firewall/wfp-state.xml", ArgsPrefix: "wfp show state"),
            (Name: "WFP filters", Relative: "07-firewall/wfp-filters.xml", ArgsPrefix: "wfp show filters verbose=on"),
            (Name: "WFP system ports", Relative: "07-firewall/wfp-sysports.xml", ArgsPrefix: "wfp show sysports")
        };

        DiagnosticStepRecord RunOne((string Name, string Relative, string ArgsPrefix) job)
        {
            string path = Path.Combine(root, job.Relative.Replace('/', Path.DirectorySeparatorChar));
            var sw = Stopwatch.StartNew();
            string args = $"{job.ArgsPrefix} file=\"{path}\"";
            string output = ProcessRunner.RunNative("netsh.exe", args, out string error, out int exitCode, 45000, cancellationToken);
            sw.Stop();

            bool hasFile = File.Exists(path) && new FileInfo(path).Length > 0;
            string status = exitCode == 0 && hasFile ? "OK" : "WARN";
            string control = $"Command: netsh {args}\r\nExitCode: {exitCode}\r\nDuration: {sw.Elapsed.TotalSeconds:F2}s\r\n" +
                             $"Output file: {path}\r\nExists: {hasFile}\r\n\r\n{output}\r\n{error}";
            WriteText(root, job.Relative + ".log.txt", control);

            return new DiagnosticStepRecord
            {
                Name = $"导出 {job.Name}",
                File = job.Relative,
                Status = status,
                ExitCode = exitCode,
                DurationMs = sw.ElapsedMilliseconds,
                Error = status == "OK" ? "" : (string.IsNullOrWhiteSpace(error) ? "WFP 输出文件未生成或为空" : error)
            };
        }

        // 只读查询彼此独立；并发执行把最坏等待从 3×45 秒压到约 45 秒。
        var tasks = jobs.Select(job => Task.Run(() => RunOne(job), cancellationToken)).ToArray();
        return Task.WhenAll(tasks).GetAwaiter().GetResult().ToList();
    }

    private static List<DiagnosticStepRecord> RunConditionalTraceroutes(
        string root,
        List<PingProbeResult> pings,
        CancellationToken cancellationToken)
    {
        bool publicIpv4Responded = pings.Any(p =>
            p.Kind.StartsWith("Public IPv4", StringComparison.Ordinal) && p.Received > 0);

        if (!publicIpv4Responded && !HasUsableIpv4DefaultRoute())
        {
            const string reason =
                "跳过公网 Traceroute：当前没有可用 IPv4 默认网关，且公共 IPv4 Ping 无响应。此时 Traceroute 对定位 DHCP/APIPA/本地链路故障价值较低。";
            WriteText(root, "05-connectivity/traceroute-skipped.txt", reason);
            return new List<DiagnosticStepRecord>
            {
                new()
                {
                    Name = "公网 Traceroute",
                    File = "05-connectivity/traceroute-skipped.txt",
                    Status = "SKIP",
                    ExitCode = 0,
                    Error = reason
                }
            };
        }

        var targets = new[]
        {
            (Target: "223.5.5.5", File: "05-connectivity/traceroute-223.5.5.5.txt", Name: "Traceroute 到阿里 DNS 223.5.5.5"),
            (Target: "1.1.1.1", File: "05-connectivity/traceroute-1.1.1.1.txt", Name: "Traceroute 到 1.1.1.1")
        };

        // -d 避免逐跳反向 DNS；12 hops * 3 probes * 400ms，完全断网时单条约 15 秒封顶。
        var tasks = targets.Select(t => Task.Run(() =>
            RunNativeStep(root, t.File, t.Name, "tracert.exe",
                $"-d -h 12 -w 400 {t.Target}", 20000, cancellationToken), cancellationToken)).ToArray();

        return Task.WhenAll(tasks).GetAwaiter().GetResult().ToList();
    }

    private static bool HasUsableIpv4DefaultRoute()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            try
            {
                var props = ni.GetIPProperties();
                bool hasUsableV4 = props.UnicastAddresses.Any(a =>
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(a.Address))
                        return false;
                    byte[] b = a.Address.GetAddressBytes();
                    return !(b.Length == 4 && b[0] == 169 && b[1] == 254);
                });

                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));

                if (hasUsableV4 && hasGateway)
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static void WriteCommandResult(
        string root, string relative, string title, string command, string output, string error, int exitCode, TimeSpan duration)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine($"# Collected: {DateTimeOffset.Now:O}");
        sb.AppendLine($"# Command: {command}");
        sb.AppendLine($"# ExitCode: {exitCode}");
        sb.AppendLine($"# Duration: {duration.TotalSeconds:F2}s");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(output))
            sb.AppendLine(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(error))
        {
            sb.AppendLine();
            sb.AppendLine("=== STDERR / ERROR ===");
            sb.AppendLine(error.TrimEnd());
        }
        WriteText(root, relative, sb.ToString());
    }

    private static void WriteHealthSnapshot(string root, List<NetworkHealthItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Risk\tCategory\tName\tStatus\tDetail");
        foreach (var i in items)
            sb.AppendLine($"{i.Risk}\t{EscapeTsv(i.Category)}\t{EscapeTsv(i.Name)}\t{EscapeTsv(i.Status)}\t{EscapeTsv(i.Detail)}");
        WriteText(root, "00-summary/network-health.tsv", sb.ToString());
    }

    private static List<TrafficSnapshot> TakeTrafficSnapshot()
    {
        var result = new List<TrafficSnapshot>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            try
            {
                var s = ni.GetIPStatistics();
                result.Add(new TrafficSnapshot
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Speed = ni.Speed,
                    BytesReceived = s.BytesReceived,
                    BytesSent = s.BytesSent,
                    UnicastReceived = s.UnicastPacketsReceived,
                    UnicastSent = s.UnicastPacketsSent,
                    NonUnicastReceived = s.NonUnicastPacketsReceived,
                    NonUnicastSent = s.NonUnicastPacketsSent,
                    IncomingErrors = s.IncomingPacketsWithErrors,
                    OutgoingErrors = s.OutgoingPacketsWithErrors,
                    IncomingDiscarded = s.IncomingPacketsDiscarded,
                    OutgoingDiscarded = s.OutgoingPacketsDiscarded
                });
            }
            catch { }
        }
        return result;
    }

    private static List<TrafficDelta> CaptureTrafficSample(string root, int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = TakeTrafficSnapshot();
        if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
            throw new OperationCanceledException(cancellationToken);
        var after = TakeTrafficSnapshot();
        var list = new List<TrafficDelta>();
        var sb = new StringBuilder();
        sb.AppendLine("Adapter\tRxMbps\tTxMbps\tRxPackets/s\tTxPackets/s\tNonUnicastRx/s\tErrorsDelta\tDiscardsDelta");

        foreach (var a in after)
        {
            var b = before.FirstOrDefault(x => x.Id == a.Id);
            if (b == null) continue;

            long rxPackets = Math.Max(0, (a.UnicastReceived + a.NonUnicastReceived) - (b.UnicastReceived + b.NonUnicastReceived));
            long txPackets = Math.Max(0, (a.UnicastSent + a.NonUnicastSent) - (b.UnicastSent + b.NonUnicastSent));
            long nonUnicast = Math.Max(0, a.NonUnicastReceived - b.NonUnicastReceived);
            long errors = Math.Max(0, (a.IncomingErrors + a.OutgoingErrors) - (b.IncomingErrors + b.OutgoingErrors));
            long discards = Math.Max(0, (a.IncomingDiscarded + a.OutgoingDiscarded) - (b.IncomingDiscarded + b.OutgoingDiscarded));

            var d = new TrafficDelta
            {
                Name = a.Name,
                RxMbps = Math.Max(0, a.BytesReceived - b.BytesReceived) * 8.0 / seconds / 1_000_000,
                TxMbps = Math.Max(0, a.BytesSent - b.BytesSent) * 8.0 / seconds / 1_000_000,
                RxPacketsPerSecond = rxPackets / (double)seconds,
                TxPacketsPerSecond = txPackets / (double)seconds,
                NonUnicastRxPerSecond = nonUnicast / (double)seconds,
                ErrorDelta = errors,
                DiscardDelta = discards
            };
            list.Add(d);
            sb.AppendLine($"{EscapeTsv(d.Name)}\t{d.RxMbps:F3}\t{d.TxMbps:F3}\t{d.RxPacketsPerSecond:F1}\t{d.TxPacketsPerSecond:F1}\t{d.NonUnicastRxPerSecond:F1}\t{errors}\t{discards}");
        }

        WriteText(root, "02-adapters/statistics-sample.tsv", sb.ToString());
        return list;
    }

    private static List<PingProbeResult> RunPingProbes(string root, List<string> findings, ref int warningCount, CancellationToken cancellationToken)
    {
        var probes = new List<(string target, string kind)>();
        var gateways = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n =>
            {
                try { return n.GetIPProperties().GatewayAddresses.Select(g => g.Address.ToString()); }
                catch { return Array.Empty<string>(); }
            })
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "0.0.0.0" && s != "::")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        probes.AddRange(gateways.Select(g => (g, "Gateway")));
        probes.Add(("223.5.5.5", "Public IPv4 / AliDNS"));
        probes.Add(("119.29.29.29", "Public IPv4 / DNSPod"));
        probes.Add(("1.1.1.1", "Public IPv4 / Cloudflare"));
        probes.Add(("8.8.8.8", "Public IPv4 / Google"));
        probes.Add(("www.baidu.com", "Domain / China"));
        probes.Add(("www.qq.com", "Domain / China"));
        probes.Add(("www.microsoft.com", "Domain / Global"));

        bool hasGlobalV6 = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Any(n =>
            {
                try
                {
                    return n.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !a.Address.IsIPv6LinkLocal &&
                        !a.Address.IsIPv6SiteLocal &&
                        !IPAddress.IsLoopback(a.Address));
                }
                catch { return false; }
            });

        if (hasGlobalV6)
        {
            probes.Add(("2400:3200::1", "Public IPv6 / AliDNS"));
            probes.Add(("2606:4700:4700::1111", "Public IPv6 / Cloudflare"));
        }

        var pingTasks = probes.Distinct()
            .Select(p => Task.Run(() => PingTarget(p.target, p.kind, 5, 900, cancellationToken), cancellationToken))
            .ToArray();
        var results = Task.WhenAll(pingTasks).GetAwaiter().GetResult().ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Target\tKind\tResolvedAddress\tSent\tReceived\tLoss%\tMinMs\tAvgMs\tMaxMs\tError");
        foreach (var r in results)
            sb.AppendLine($"{EscapeTsv(r.Target)}\t{EscapeTsv(r.Kind)}\t{EscapeTsv(r.ResolvedAddress)}\t{r.Sent}\t{r.Received}\t{r.LossPercent:F1}\t{r.MinMs}\t{r.AvgMs:F1}\t{r.MaxMs}\t{EscapeTsv(r.Error)}");
        WriteText(root, "05-connectivity/ping-tests.tsv", sb.ToString());

        foreach (var g in results.Where(r => r.Kind == "Gateway" && r.Received > 0 && r.LossPercent >= 20))
        {
            findings.Add($"[警告] 网关 {g.Target} 可达但丢包 {g.LossPercent:F0}%，本地链路质量异常。");
            warningCount++;
        }

        return results;
    }

    private static PingProbeResult PingTarget(string target, string kind, int count, int timeoutMs, CancellationToken cancellationToken)
    {
        var result = new PingProbeResult { Target = target, Kind = kind, Sent = count };
        var times = new List<long>();

        try
        {
            string address = target;
            if (!IPAddress.TryParse(target, out _))
            {
                var addrs = ResolveHostWithTimeout(target, 4000);
                result.ResolvedAddress = string.Join(",", addrs.Select(a => a.ToString()).Take(6));
                var preferred = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
                if (preferred == null)
                {
                    result.Error = "DNS returned no address";
                    return result;
                }
                address = preferred.ToString();
            }
            else
            {
                result.ResolvedAddress = target;
            }

            using var ping = new Ping();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = ping.Send(address, timeoutMs);
                    if (reply?.Status == IPStatus.Success)
                    {
                        result.Received++;
                        times.Add(reply.RoundtripTime);
                    }
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(result.Error))
                        result.Error = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (times.Count > 0)
        {
            result.MinMs = times.Min();
            result.MaxMs = times.Max();
            result.AvgMs = times.Average();
        }
        return result;
    }

    private static List<HttpProbeResult> RunHttpProbes(string root, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            "http://www.msftconnecttest.com/connecttest.txt",
            "https://www.baidu.com/",
            "https://www.microsoft.com/"
        };

        var tasks = (from useProxy in new[] { false, true }
                     from url in urls
                     select Task.Run(() => HttpProbe(url, useProxy, cancellationToken), cancellationToken)).ToArray();
        var results = Task.WhenAll(tasks).GetAwaiter().GetResult().ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Mode\tURL\tSuccess\tStatusCode\tDurationMs\tDetail");
        foreach (var r in results)
            sb.AppendLine($"{r.Mode}\t{EscapeTsv(r.Url)}\t{r.Success}\t{r.StatusCode}\t{r.DurationMs}\t{EscapeTsv(r.Detail)}");
        WriteText(root, "05-connectivity/http-tests.tsv", sb.ToString());
        return results;
    }

    private static HttpProbeResult HttpProbe(string url, bool useProxy, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var r = new HttpProbeResult
        {
            Url = url,
            Mode = useProxy ? "System proxy/default" : "Direct (proxy disabled)"
        };

        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(7)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            r.Success = true;
            r.StatusCode = (int)response.StatusCode;
            r.Detail = response.ReasonPhrase ?? "";
        }
        catch (Exception ex)
        {
            r.Detail = ex.GetBaseException().Message;
        }
        finally
        {
            sw.Stop();
            r.DurationMs = sw.ElapsedMilliseconds;
        }
        return r;
    }

    private static IPAddress[] ResolveHostWithTimeout(string host, int timeoutMs)
    {
        var task = Dns.GetHostAddressesAsync(host);
        if (!task.Wait(timeoutMs))
            throw new TimeoutException($"DNS resolution timed out after {timeoutMs} ms: {host}");
        return task.GetAwaiter().GetResult();
    }

    private static int GetRecentIpConflictCount(CancellationToken cancellationToken)
    {
        string output = ProcessRunner.RunPowerShell(
            "$s=(Get-Date).AddHours(-48); " +
            "(Get-WinEvent -FilterHashtable @{LogName='System';Id=4199;StartTime=$s} -ErrorAction SilentlyContinue | Measure-Object).Count",
            out _, 10000, cancellationToken);
        return int.TryParse(output.Trim(), out int count) ? count : 0;
    }

    private static void AnalyzeConnectivity(
        List<string> findings,
        List<PingProbeResult> pings,
        List<HttpProbeResult> http,
        ref int warningCount)
    {
        var gateways = pings.Where(p => p.Kind == "Gateway").ToList();
        var domesticV4 = pings.Where(p => p.Target is "223.5.5.5" or "119.29.29.29").ToList();
        var publicV4 = pings.Where(p => p.Kind.StartsWith("Public IPv4", StringComparison.Ordinal)).ToList();
        var publicV6 = pings.Where(p => p.Kind.StartsWith("Public IPv6", StringComparison.Ordinal)).ToList();
        var domains = pings.Where(p => p.Kind.StartsWith("Domain", StringComparison.Ordinal)).ToList();

        bool gatewayWorks = gateways.Count == 0 || gateways.Any(p => p.Received > 0);
        bool domesticWorks = domesticV4.Any(p => p.Received > 0);
        bool anyV4Works = publicV4.Any(p => p.Received > 0);
        bool domainResolutionFailed = domains.Count > 0 && domains.All(p =>
            string.IsNullOrWhiteSpace(p.ResolvedAddress) || !string.IsNullOrWhiteSpace(p.Error));

        if (gatewayWorks && domesticV4.Count > 0 && domesticV4.All(p => p.Received == 0))
        {
            findings.Add("[警告] 网关可达，但国内公共 IPv4 均不可达：故障更可能位于默认路由之后（上游出口、运营商、防火墙或策略路由）。");
            warningCount++;
        }

        if (gateways.Count > 0 && gateways.All(p => p.Received == 0))
        {
            if (anyV4Works)
            {
                findings.Add("[提示] 网关不响应 ICMP，但公共 IPv4 可达：网关很可能只是禁用了 Ping，不应据此判定网关故障。");
            }
            else
            {
                findings.Add("[警告] 网关与公共 IPv4 都不可 Ping：优先检查物理/Wi-Fi 链路、VLAN、DHCP/IP 配置和默认网关；同时注意部分设备可能禁用 ICMP。");
                warningCount++;
            }
        }

        if (domesticWorks && domainResolutionFailed)
        {
            findings.Add("[危险] 公共 IPv4 可达但常用域名均无法完成 DNS 解析：高度提示 DNS/NRPT/Hosts/代理接管问题。");
            warningCount++;
        }

        if (publicV6.Count > 0 && anyV4Works && publicV6.All(p => p.Received == 0))
        {
            findings.Add("[警告] IPv4 公网连通，但已配置全局 IPv6 的情况下两个 IPv6 公共目标均不可达：疑似 IPv6 路由/RA/上游问题。");
            warningCount++;
        }

        if (publicV6.Any(p => p.Received > 0) && publicV4.Count > 0 && publicV4.All(p => p.Received == 0))
        {
            findings.Add("[警告] IPv6 公网可达而 IPv4 公共目标均不可达：重点检查 IPv4 DHCP、默认路由、NAT 或运营商 IPv4 出口。");
            warningCount++;
        }

        var direct = http.Where(h => h.Mode.StartsWith("Direct", StringComparison.Ordinal)).ToList();
        var proxied = http.Where(h => h.Mode.StartsWith("System proxy", StringComparison.Ordinal)).ToList();
        int directOk = direct.Count(h => h.Success);
        int proxyOk = proxied.Count(h => h.Success);

        if (direct.Count > 0 && proxied.Count > 0)
        {
            if (directOk > 0 && proxyOk == 0)
            {
                findings.Add("[警告] HTTP/HTTPS 直连至少部分成功，但系统代理路径全部失败：系统代理/PAC/本地代理端口可能配置错误或代理进程未运行。");
                warningCount++;
            }
            else if (directOk == 0 && proxyOk > 0)
            {
                findings.Add("[提示] 直连 HTTP/HTTPS 全部失败但系统代理路径可用：当前网络可能依赖代理才能出网，排障时不要仅依据代理后的浏览器访问判断物理链路。");
            }
            else if (directOk == 0 && proxyOk == 0 && anyV4Works)
            {
                findings.Add("[警告] ICMP 公网目标可达，但直连和系统代理 HTTP/HTTPS 均失败：重点检查 DNS、TCP/443、TLS、透明过滤或防火墙。");
                warningCount++;
            }
        }
    }

    private static void AnalyzeTcpPortPressure(List<string> findings, ref int warningCount)
    {
        try
        {
            var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            int timeWait = connections.Count(c => c.State == TcpState.TimeWait);
            int closeWait = connections.Count(c => c.State == TcpState.CloseWait);
            int synSent = connections.Count(c => c.State == TcpState.SynSent);
            int highUniquePorts = connections
                .Where(c => c.LocalEndPoint.Port >= 49152)
                .Select(c => c.LocalEndPoint.Port)
                .Distinct()
                .Count();

            if (highUniquePorts >= 12000 || timeWait >= 10000)
            {
                findings.Add(
                    $"[警告] TCP 高端口/TimeWait 压力较高：高端口唯一占用 {highUniquePorts}，TIME_WAIT {timeWait}。这不等同于已经耗尽，请结合 09-connections/dynamic-ports-*.txt 与 excluded-ports-*.txt 判断实际动态端口范围。");
                warningCount++;
            }

            if (closeWait >= 1000)
            {
                findings.Add(
                    $"[警告] 当前 CLOSE_WAIT 连接 {closeWait} 条，可能存在应用未及时关闭 socket 的问题；请结合 tcp-connections.csv 和 tcp-state-summary.txt 定位进程。");
                warningCount++;
            }

            if (synSent >= 1000)
            {
                findings.Add(
                    $"[警告] 当前 SYN_SENT 连接 {synSent} 条，可能存在大量连接超时、上游不可达或防火墙静默丢包。");
                warningCount++;
            }
        }
        catch { }
    }

    private static void AddStructuralFindings(List<string> findings, ref int warningCount)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();
        var up = adapters.Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();

        if (up.Count == 0)
        {
            findings.Add("[危险] 没有检测到处于 Up 状态的非环回网络适配器。");
            warningCount++;
            return;
        }

        bool anyIpv4 = false;
        bool anyIpv4Gateway = false;
        bool anyGlobalIpv6 = false;
        bool anyIpv6Gateway = false;
        bool anyDns = false;

        foreach (var ni in up)
        {
            try
            {
                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .ToList();

                anyIpv4 |= v4.Count > 0;
                anyIpv4Gateway |= props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));

                anyGlobalIpv6 |= props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                    !a.Address.IsIPv6LinkLocal &&
                    !a.Address.IsIPv6SiteLocal &&
                    !IPAddress.IsLoopback(a.Address));
                anyIpv6Gateway |= props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                    !g.Address.Equals(IPAddress.IPv6Any));

                anyDns |= props.DnsAddresses.Count > 0;

                foreach (var ip in v4)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b.Length == 4 && b[0] == 169 && b[1] == 254)
                    {
                        findings.Add($"[危险] {ni.Name} 获得 APIPA 地址 {ip}，高度提示 DHCP 获取失败。");
                        warningCount++;
                    }
                }
            }
            catch { }
        }

        if (!anyIpv4)
        {
            if (anyGlobalIpv6)
            {
                findings.Add(anyIpv6Gateway
                    ? "[提示] 当前活动网卡没有 IPv4，但存在全局 IPv6 地址和 IPv6 网关；可能是正常的 IPv6-only / IPv6-first 网络。"
                    : "[警告] 当前没有 IPv4，虽存在全局 IPv6 地址，但未检测到 IPv6 网关；需检查 IPv6 RA/默认路由。");
                if (!anyIpv6Gateway) warningCount++;
            }
            else
            {
                findings.Add("[警告] 所有活动网卡均没有可用 IPv4 或全局 IPv6 地址。");
                warningCount++;
            }
        }
        else if (!anyIpv4Gateway)
        {
            findings.Add("[警告] 存在 IPv4 地址，但活动网卡没有检测到 IPv4 默认网关。");
            warningCount++;
        }
        if (!anyDns)
        {
            findings.Add("[警告] 活动网卡没有检测到 DNS 服务器。");
            warningCount++;
        }

        try
        {
            ResolveHostWithTimeout("www.baidu.com", 4000);
        }
        catch
        {
            findings.Add("[警告] Windows 系统解析器无法解析 www.baidu.com；请重点查看 04-dns 与 network-health.tsv。");
            warningCount++;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static DiagnosticStepRecord CapturePktMon(string root, int seconds, CancellationToken cancellationToken)
    {
        string dir = EnsureDirectory(root, "11-packet-capture");

        if (!IsAdministrator())
        {
            const string message = "PktMon 需要管理员权限；当前进程未提升，已跳过抓包。";
            WriteText(root, "11-packet-capture/pktmon-control.txt", message);
            return new DiagnosticStepRecord
            {
                Name = "PktMon 抓包",
                File = "11-packet-capture/pktmon-control.txt",
                Status = "SKIP",
                ExitCode = 0,
                Error = message
            };
        }

        string pktmonExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pktmon.exe");
        if (!File.Exists(pktmonExe))
        {
            const string message = "系统中未找到 pktmon.exe；可能是旧版/精简版 Windows，已跳过抓包。";
            WriteText(root, "11-packet-capture/pktmon-control.txt", message);
            return new DiagnosticStepRecord
            {
                Name = "PktMon 抓包",
                File = "11-packet-capture/pktmon-control.txt",
                Status = "SKIP",
                ExitCode = 0,
                Error = message
            };
        }
        string etl = Path.Combine(dir, "pktmon.etl");
        string txt = Path.Combine(dir, "pktmon.txt");
        string pcapng = Path.Combine(dir, "pktmon.pcapng");
        var sw = Stopwatch.StartNew();
        var log = new StringBuilder();

        string startArgs = $"start --capture --pkt-size 128 --file-name \"{etl}\" --file-size 32";
        string startOutput = ProcessRunner.RunNative(pktmonExe, startArgs, out string startError, out int startCode, 15000);
        log.AppendLine("=== pktmon start ===");
        log.AppendLine(startOutput);
        log.AppendLine(startError);

        if (startCode != 0)
        {
            sw.Stop();
            WriteText(root, "11-packet-capture/pktmon-control.txt", log.ToString());
            return new DiagnosticStepRecord
            {
                Name = "PktMon 抓包",
                File = "11-packet-capture/pktmon.etl",
                Status = "WARN",
                ExitCode = startCode,
                DurationMs = sw.ElapsedMilliseconds,
                Error = startError
            };
        }

        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));

            string counters = ProcessRunner.RunNative(
                pktmonExe,
                "counters --type drop --include-hidden --drop-reason --json",
                out string countersError,
                out int countersCode,
                15000);
            var countersText = new StringBuilder();
            countersText.AppendLine($"ExitCode: {countersCode}");
            if (!string.IsNullOrWhiteSpace(counters))
                countersText.AppendLine(counters.TrimEnd());
            if (!string.IsNullOrWhiteSpace(countersError))
            {
                countersText.AppendLine();
                countersText.AppendLine("=== STDERR / ERROR ===");
                countersText.AppendLine(countersError.TrimEnd());
            }
            WriteText(root, "11-packet-capture/pktmon-drop-counters.json.txt", countersText.ToString());
        }
        finally
        {
            string stopOutput = ProcessRunner.RunNative(pktmonExe, "stop", out string stopError, out int stopCode, 15000);
            log.AppendLine("=== pktmon stop ===");
            log.AppendLine(stopOutput);
            log.AppendLine(stopError);
            log.AppendLine($"stop exit={stopCode}");
        }

        if (File.Exists(etl))
        {
            string txtArgs = $"etl2txt \"{etl}\" --out \"{txt}\"";
            string conv = ProcessRunner.RunNative(pktmonExe, txtArgs, out string convErr, out int convCode, 30000);
            log.AppendLine("=== pktmon etl2txt ===");
            log.AppendLine(conv);
            log.AppendLine(convErr);
            log.AppendLine($"exit={convCode}");

            string pcapArgs = $"etl2pcap \"{etl}\" --out \"{pcapng}\"";
            string pcap = ProcessRunner.RunNative(pktmonExe, pcapArgs, out string pcapErr, out int pcapCode, 30000);
            log.AppendLine("=== pktmon etl2pcap ===");
            log.AppendLine(pcap);
            log.AppendLine(pcapErr);
            log.AppendLine($"exit={pcapCode}");
        }

        sw.Stop();
        WriteText(root, "11-packet-capture/pktmon-control.txt", log.ToString());
        return new DiagnosticStepRecord
        {
            Name = "PktMon 抓包",
            File = "11-packet-capture/pktmon.etl",
            Status = File.Exists(etl) ? "OK" : "WARN",
            ExitCode = File.Exists(etl) ? 0 : 1,
            DurationMs = sw.ElapsedMilliseconds,
            Error = File.Exists(etl) ? "" : "PktMon 未生成 ETL 文件"
        };
    }

    private static void WriteSummary(
        string root,
        DateTimeOffset started,
        List<string> findings,
        List<NetworkHealthItem> healthItems,
        List<PingProbeResult> pingResults,
        List<HttpProbeResult> httpResults,
        List<TrafficDelta> trafficDeltas,
        List<DiagnosticStepRecord> records,
        DiagnosticBundleOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# WinNetManager 深度网络诊断报告");
        sb.AppendLine();
        sb.AppendLine($"- 采集时间：{started:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- 计算机名：{Environment.MachineName}");
        sb.AppendLine($"- WinNetManager：{Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"- 管理员权限：{(IsAdministrator() ? "是" : "否（部分系统级采集可能降级）")}");
        sb.AppendLine($"- PktMon 抓包：{(options.IncludePacketCapture ? $"是（{options.PacketCaptureSeconds} 秒）" : "否")}");
        sb.AppendLine();

        sb.AppendLine("## 自动判读");
        if (findings.Count == 0)
            sb.AppendLine("- 未发现明确的结构性故障。请结合 ping、traceroute、DNS、事件日志和网络健康扫描继续判断。");
        else
            foreach (string finding in findings.Distinct())
                sb.AppendLine($"- {finding}");
        sb.AppendLine();

        var risky = healthItems.Where(i => i.Risk is "Danger" or "Warn").ToList();
        sb.AppendLine("## 网络健康扫描中的危险 / 警告");
        if (risky.Count == 0)
            sb.AppendLine("- 无。");
        else
            foreach (var i in risky)
                sb.AppendLine($"- [{i.Risk}] {i.Category} / {i.Name}：{i.Status} — {i.Detail}");
        sb.AppendLine();

        sb.AppendLine("## Ping 摘要");
        sb.AppendLine("| 目标 | 类型 | 收/发 | 丢包 | 平均延迟 |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (var p in pingResults)
            sb.AppendLine($"| {Md(p.Target)} | {Md(p.Kind)} | {p.Received}/{p.Sent} | {p.LossPercent:F0}% | {(p.Received > 0 ? $"{p.AvgMs:F1} ms" : "-")} |");
        sb.AppendLine();

        sb.AppendLine("## HTTP / HTTPS 摘要");
        sb.AppendLine("| 路径 | URL | 结果 | 耗时 |");
        sb.AppendLine("|---|---|---|---:|");
        foreach (var h in httpResults)
            sb.AppendLine($"| {Md(h.Mode)} | {Md(h.Url)} | {(h.Success ? $"HTTP {h.StatusCode}" : Md(h.Detail))} | {h.DurationMs} ms |");
        sb.AppendLine();

        sb.AppendLine("## 5 秒接口流量采样");
        sb.AppendLine("| 网卡 | RX | TX | RX pps | 非单播 RX pps | 错误 | 丢弃 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var d in trafficDeltas)
            sb.AppendLine($"| {Md(d.Name)} | {d.RxMbps:F2} Mbps | {d.TxMbps:F2} Mbps | {d.RxPacketsPerSecond:F0} | {d.NonUnicastRxPerSecond:F0} | {d.ErrorDelta} | {d.DiscardDelta} |");
        sb.AppendLine();

        sb.AppendLine("## 采集步骤");
        sb.AppendLine("| 状态 | 项目 | 文件 | 耗时 |");
        sb.AppendLine("|---|---|---|---:|");
        foreach (var r in records)
            sb.AppendLine($"| {r.Status} | {Md(r.Name)} | {Md(r.File)} | {r.DurationMs / 1000.0:F1}s |");
        sb.AppendLine();
        sb.AppendLine("> 诊断包可能包含 IP/MAC、SSID/BSSID、DNS/代理/VPN 地址、活动连接、应用路径、SID、WFP 配置和网络事件。发送给第三方前请确认接收方可信。不会主动采集 Wi-Fi 密码、浏览器 Cookie、VPN 密码或文件内容；DNS 缓存也默认不采集。");

        WriteText(root, "SUMMARY.md", sb.ToString());
    }

    private static void WriteManifest(
        string root,
        DateTimeOffset started,
        List<DiagnosticStepRecord> records,
        DiagnosticBundleOptions options)
    {
        var manifest = new
        {
            schema = 1,
            generated_at = started,
            computer_name = Environment.MachineName,
            app_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            elevated = IsAdministrator(),
            packet_capture = options.IncludePacketCapture,
            packet_capture_seconds = options.PacketCaptureSeconds,
            steps = records
        };

        WriteText(root, "manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void WritePackageReadme(string root, DiagnosticBundleOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinNetManager 深度网络诊断包");
        sb.AppendLine("============================");
        sb.AppendLine();
        sb.AppendLine("用途：在无法远程连接目标电脑时，一次性采集网络故障定位所需的本机状态与链路证据。");
        sb.AppendLine("此模式默认只读，不修改 DNS、IP、路由、防火墙，不重启网卡和服务。");
        sb.AppendLine();
        sb.AppendLine("主要内容：");
        sb.AppendLine("- Windows / 更新 / 时间同步");
        sb.AppendLine("- 全部网卡、驱动、绑定、高级属性、IPv4/IPv6、DHCP、网关、MTU、跃点");
        sb.AppendLine("- 活动与持久路由、ARP/IPv6 Neighbor");
        sb.AppendLine("- DNS、NRPT、Hosts、系统解析与逐 DNS 服务器解析测试");
        sb.AppendLine("- 网关/公共 IPv4/IPv6/常用域名 Ping 与丢包");
        sb.AppendLine("- HTTP/HTTPS 直连与系统代理路径");
        sb.AppendLine("- 条件化并发 Traceroute、TCP 动态/排除端口范围、连接表、监听端点");
        sb.AppendLine("- WinINET/PAC、VPN、网络接管软件、Winsock、完整 WFP state/filters、驱动线索");
        sb.AppendLine("- 防火墙配置与启用规则");
        sb.AppendLine("- DHCP 租约注册表、DHCP trace、最近 48 小时 DHCP/DNS/WLAN/网络/IP 冲突事件");
        sb.AppendLine("- 5 秒网卡流量/错误/丢弃/非单播采样，用于发现拥塞或疑似广播风暴");
        sb.AppendLine(options.IncludePacketCapture
            ? $"- 额外包含 {options.PacketCaptureSeconds} 秒 PktMon 抓包（128 字节截断）。"
            : "- 未包含抓包；如需要分析广播/ARP/异常流量，可在生成时勾选 PktMon。");
        sb.AppendLine();
        sb.AppendLine("隐私：包内可能出现 IP、MAC、SSID/BSSID、代理/VPN 服务器、进程名、应用路径、SID、活动连接和网络事件。");
        sb.AppendLine("WFP XML 与事件日志可能包含本机应用路径或安全主体信息。不会主动采集 Wi-Fi 密码、浏览器 Cookie、VPN 密码、文档内容或环境变量；DNS Cache 默认不采集。");
        WriteText(root, "README.txt", sb.ToString());
    }

    private static void WriteText(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, text ?? "", Utf8);
    }

    private static string EnsureDirectory(string root, string relative)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string EscapeTsv(string? value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

    private static string Md(string? value) =>
        (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
