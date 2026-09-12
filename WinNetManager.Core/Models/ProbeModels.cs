
using System.Collections.Concurrent;

namespace WinNetManager.Core.Models;

/// <summary>一个探针请求。同 Key 的请求在单次 tick 内去重共享结果。</summary>
public sealed class ProbeRequest
{
    public ProbeKind Kind { get; set; }
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv4;
    /// <summary>ping 目标 / TCP host。空表示不适用。</summary>
    public string Target { get; set; } = "";
    public int TcpPort { get; set; }
    public int TimeoutMs { get; set; } = 3000;

    public string Key => $"{Kind}|{AdapterId}|{Family}|{Target}|{TcpPort}|{TimeoutMs}";

    public override string ToString() => Key;
}

public sealed class ProbeResult
{
    /// <summary>True=可达/Up/有地址；False=不可达/异常；Unknown=探测自身异常。</summary>
    public TriState State { get; set; } = TriState.Unknown;
    public string Reason { get; set; } = "";
    public long RttMs { get; set; } = -1;
    public DateTime Timestamp { get; set; }
}

/// <summary>一次 tick 的观测快照：网卡快照 + 探针结果（按 Key）。</summary>
public sealed class ProbeSnapshot
{
    public DateTime Timestamp { get; set; }
    public ConcurrentDictionary<string, AdapterSnapshot> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ProbeResult> Results { get; } = new(StringComparer.Ordinal);

    public AdapterSnapshot? GetAdapter(string id)
        => Adapters.TryGetValue(id, out var a) ? a : null;
}
