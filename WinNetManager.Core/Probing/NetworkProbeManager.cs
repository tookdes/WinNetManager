
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Probing;

/// <summary>
/// 生产探针实现：
/// - 枚举全部网卡（含 WWAN/PPP，修复现有 DhcpManager 漏掉 4G 卡的问题），以 NetworkInterface.Id 为稳定键；
/// - ping 绑定网卡当前稳定全局源地址（ping.exe -S），避免弱主机模型下从其他网卡发出造成误判；
/// - TCP 探针用于代理/端口可达性。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NetworkProbeManager : IProbeSource, IAdapterLocator
{
    private readonly IShell _shell;

    public NetworkProbeManager(IShell shell) => _shell = shell;

    public IReadOnlyList<AdapterSnapshot> GetAdapters()
    {
        var list = new List<AdapterSnapshot>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel &&
                    (ni.GetIPProperties()?.UnicastAddresses == null ||
                     ni.GetIPProperties()!.UnicastAddresses.Count == 0)) continue;
                try
                {
                    var props = ni.GetIPProperties();
                    var snap = new AdapterSnapshot
                    {
                        Id = ni.Id,
                        Name = ni.Name,
                        Description = ni.Description ?? "",
                        Type = ni.NetworkInterfaceType.ToString(),
                        Up = ni.OperationalStatus == OperationalStatus.Up,
                        GlobalIpv4 = PickStableAddress(props.UnicastAddresses, AddressFamily.InterNetwork),
                        GlobalIpv6 = PickStableAddress(props.UnicastAddresses, AddressFamily.InterNetworkV6),
                        Ipv4Gateway = props.GatewayAddresses
                            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(g => g.Address.ToString())
                            .FirstOrDefault(),
                        Ipv6Gateway = props.GatewayAddresses
                            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6)
                            .Select(g => g.Address.ToString())
                            .FirstOrDefault(),
                    };
                    // 完全没有地址的隧道/虚拟网卡不展示，减少噪音
                    if (snap.GlobalIpv4 == null && snap.GlobalIpv6 == null &&
                        snap.Ipv4Gateway == null && snap.Ipv6Gateway == null &&
                        !snap.Up) continue;
                    list.Add(snap);
                }
                catch { /* 单网卡枚举失败不影响整体 */ }
            }
        }
        catch { /* 权限或平台异常 */ }
        return list;
    }

    public AdapterSnapshot? GetAdapter(string adapterId)
        => GetAdapters().FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase));

    public async Task<ProbeSnapshot> ProbeAsync(IReadOnlyCollection<ProbeRequest> requests, CancellationToken ct)
    {
        var snapshot = new ProbeSnapshot { Timestamp = DateTime.Now };
        foreach (var a in GetAdapters())
            snapshot.Adapters[a.Id] = a;

        // 去重
        var unique = requests
            .GroupBy(r => r.Key)
            .Select(g => g.First())
            .ToList();

        // 并行执行
        var tasks = unique.Select(r => RunOneAsync(snapshot, r, ct));
        await Task.WhenAll(tasks);
        return snapshot;
    }

    private async Task RunOneAsync(ProbeSnapshot snapshot, ProbeRequest req, CancellationToken ct)
    {
        try
        {
            switch (req.Kind)
            {
                case ProbeKind.AdapterUp:
                    snapshot.Results[req.Key] = EvaluateAdapterUp(snapshot, req);
                    break;
                case ProbeKind.HasGlobalAddress:
                    snapshot.Results[req.Key] = EvaluateHasAddress(snapshot, req);
                    break;
                case ProbeKind.Ping:
                    snapshot.Results[req.Key] = await PingAsync(snapshot, req, ct);
                    break;
                case ProbeKind.TcpConnect:
                    snapshot.Results[req.Key] = await TcpConnectAsync(snapshot, req, ct);
                    break;
                default:
                    snapshot.Results[req.Key] = new ProbeResult { State = TriState.Unknown, Reason = "不支持的探针类型" };
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            snapshot.Results[req.Key] = new ProbeResult { State = TriState.Unknown, Reason = "已取消" };
        }
        catch (Exception ex)
        {
            snapshot.Results[req.Key] = new ProbeResult { State = TriState.Unknown, Reason = ex.Message };
        }
    }

    private static ProbeResult EvaluateAdapterUp(ProbeSnapshot snapshot, ProbeRequest req)
    {
        var a = snapshot.GetAdapter(req.AdapterId);
        if (a == null) return new ProbeResult { State = TriState.Unknown, Reason = "网卡不存在" };
        return a.Up
            ? new ProbeResult { State = TriState.True, Reason = "Up" }
            : new ProbeResult { State = TriState.False, Reason = "Down" };
    }

    private static ProbeResult EvaluateHasAddress(ProbeSnapshot snapshot, ProbeRequest req)
    {
        var a = snapshot.GetAdapter(req.AdapterId);
        if (a == null) return new ProbeResult { State = TriState.Unknown, Reason = "网卡不存在" };
        var addr = a.GetGlobalAddress(req.Family);
        return addr != null
            ? new ProbeResult { State = TriState.True, Reason = addr }
            : new ProbeResult { State = TriState.False, Reason = "无全局地址" };
    }

    private async Task<ProbeResult> PingAsync(ProbeSnapshot snapshot, ProbeRequest req, CancellationToken ct)
    {
        var a = snapshot.GetAdapter(req.AdapterId);
        if (a == null) return new ProbeResult { State = TriState.Unknown, Reason = "网卡不存在" };
        var src = a.GetGlobalAddress(req.Family);
        if (src == null) return new ProbeResult { State = TriState.Unknown, Reason = "网卡无源地址" };
        if (string.IsNullOrWhiteSpace(req.Target)) return new ProbeResult { State = TriState.Unknown, Reason = "目标为空" };

        var args = new List<string>
        {
            req.Family == AddressFamilyKind.IPv4 ? "-4" : "-6",
            "-n", "2", "-w", req.TimeoutMs.ToString(), "-S", src, req.Target.Trim(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string output = "";
        string err = "";
        int exitCode = -2;
        await Task.Run(() => { output = _shell.Run("ping.exe", args, out err, out exitCode, req.TimeoutMs * 2 + 3000); }, ct);
        sw.Stop();

        if (exitCode == 0)
            return new ProbeResult { State = TriState.True, Reason = "可达", RttMs = sw.ElapsedMilliseconds };
        if (exitCode == -2)
            return new ProbeResult { State = TriState.Unknown, Reason = "ping 超时被杀" };
        // exit 1 = 不可达/超时
        return new ProbeResult { State = TriState.False, Reason = $"exit={exitCode}" };
    }

    private static async Task<ProbeResult> TcpConnectAsync(ProbeSnapshot snapshot, ProbeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Target))
            return new ProbeResult { State = TriState.Unknown, Reason = "目标为空" };
        string host = req.Target.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];

        IPAddress? ip = IPAddress.TryParse(host, out var parsed) ? parsed : null;
        string? bindIp = null;
        if (!string.IsNullOrWhiteSpace(req.AdapterId))
        {
            var a = snapshot.GetAdapter(req.AdapterId);
            bindIp = a?.GetGlobalAddress(req.Family);
            if (bindIp == null && a != null)
                return new ProbeResult { State = TriState.Unknown, Reason = "出口网卡无源地址" };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(req.TimeoutMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using (socket)
            {
                if (bindIp != null)
                {
                    var bindAddr = IPAddress.Parse(bindIp);
                    socket.Bind(new IPEndPoint(bindAddr, 0));
                }
                if (ip != null)
                    await socket.ConnectAsync(new IPEndPoint(ip, req.TcpPort), cts.Token);
                else
                    await socket.ConnectAsync(host, req.TcpPort, cts.Token);
            }
            sw.Stop();
            return new ProbeResult { State = TriState.True, Reason = "TCP 连接成功", RttMs = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult { State = TriState.False, Reason = "TCP 超时" };
        }
        catch (Exception ex)
        {
            return new ProbeResult { State = TriState.False, Reason = $"连接失败: {ex.Message}" };
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? PickStableAddress(IEnumerable<UnicastIPAddressInformation> addrs, AddressFamily family)
    {
        var candidates = addrs.Where(a => a.Address.AddressFamily == family).ToList();
        if (candidates.Count == 0) return null;

        if (family == AddressFamily.InterNetwork)
        {
            foreach (var a in candidates)
            {
                var b = a.Address.GetAddressBytes();
                if (b[0] == 127) continue;          // loopback
                if (b[0] == 169 && b[1] == 254) continue; // APIPA
                if (b[0] == 0) continue;             // 0.0.0.0
                return a.Address.ToString();
            }
            return null;
        }

        // IPv6：仅全局单播 2000::/3；优先选非隐私（SuffixOrigin != Random）的稳定地址，
        // 避免临时地址过期后 ping -S 绑定失败导致探测恒为 Unknown
        var global = candidates
            .Where(a => !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6SiteLocal && !a.Address.IsIPv6Multicast)
            .Where(a => { var b = a.Address.GetAddressBytes(); return (b[0] & 0xE0) == 0x20; })
            .OrderBy(a => a.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.Random ? 1 : 0)
            .ToList();
        return global.FirstOrDefault()?.Address.ToString();
    }
}
