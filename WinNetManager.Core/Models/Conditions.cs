
using System.Text.Json.Serialization;
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingFailCondition), "pingFails")]
[JsonDerivedType(typeof(PingSucceedsCondition), "pingSucceeds")]
[JsonDerivedType(typeof(AdapterUpCondition), "adapterUp")]
[JsonDerivedType(typeof(AdapterHasAddressCondition), "adapterHasAddress")]
[JsonDerivedType(typeof(ProfileExistsCondition), "profileExists")]
[JsonDerivedType(typeof(ProfileLastConnectedAfterCondition), "profileAfter")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
public abstract class ConditionBase
{
    /// <summary>三态求值。不得抛异常，内部异常应转为 Unknown。</summary>
    public abstract TriState Evaluate(ConditionContext ctx);

    /// <summary>把本条件（含子条件）需要的探针请求收集进集合，供共享去重。</summary>
    public abstract void CollectProbeRequests(ICollection<ProbeRequest> into);

    /// <summary>人读摘要（UI 展示 / 日志）。</summary>
    public abstract string Describe();
}

/// <summary>ping 目标失败（该网卡经指定源地址无法到达目标）。</summary>
public sealed class PingFailCondition : ConditionBase
{
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv6;
    public List<string> Targets { get; set; } = new();
    public PingAggregateMode Mode { get; set; } = PingAggregateMode.All;
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// 网卡没有该地址族的全局地址时是否视为"失败"。
    /// 默认 true：光猫重拨后最常见的故障就是 IPv6 GUA 丢失/只剩脏地址，
    /// 此时按失败处理才能触发自愈；设为 false 则按 Unknown（不累计）处理。
    /// </summary>
    public bool TreatMissingAddressAsFailure { get; set; } = true;

    public override void CollectProbeRequests(ICollection<ProbeRequest> into)
    {
        foreach (var t in Targets)
        {
            var t2 = t.Trim();
            if (t2.Length == 0) continue;
            into.Add(new ProbeRequest { Kind = ProbeKind.Ping, AdapterId = AdapterId, Family = Family, Target = t2, TimeoutMs = TimeoutMs });
        }
    }

    public override TriState Evaluate(ConditionContext ctx)
    {
        if (ctx.IsAdapterSuppressed(AdapterId)) return TriState.Unknown;
        var adapter = ctx.Snapshot.GetAdapter(AdapterId);
        if (adapter == null) return TriState.Unknown;
        var src = adapter.GetGlobalAddress(Family);
        if (src == null)
            return TreatMissingAddressAsFailure ? TriState.True : TriState.Unknown; // 无地址 = 该族不可用，视为失败

        var states = new List<TriState>();
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue; // 空目标不参与判定，避免永久 Unknown
            var key = new ProbeRequest { Kind = ProbeKind.Ping, AdapterId = AdapterId, Family = Family, Target = t.Trim(), TimeoutMs = TimeoutMs }.Key;
            states.Add(ctx.Snapshot.Results.TryGetValue(key, out var r) ? r.State : TriState.Unknown);
        }
        if (states.Count == 0) return TriState.Unknown;

        if (Mode == PingAggregateMode.All)
        {
            if (states.All(s => s == TriState.False)) return TriState.True;
            if (states.Any(s => s == TriState.True)) return TriState.False;
            return TriState.Unknown;
        }
        else
        {
            if (states.Any(s => s == TriState.False)) return TriState.True;
            if (states.All(s => s == TriState.True)) return TriState.False;
            return TriState.Unknown;
        }
    }

    public override string Describe()
        => $"{Family} ping 失败: {string.Join(" / ", Targets)} ({(Mode == PingAggregateMode.All ? "全部" : "任一")})";
}

/// <summary>ping 目标成功（与 PingFail 相反）。</summary>
public sealed class PingSucceedsCondition : ConditionBase
{
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv6;
    public List<string> Targets { get; set; } = new();
    public PingAggregateMode Mode { get; set; } = PingAggregateMode.All;
    public int TimeoutMs { get; set; } = 3000;

    public override void CollectProbeRequests(ICollection<ProbeRequest> into)
    {
        foreach (var t in Targets)
        {
            var t2 = t.Trim();
            if (t2.Length == 0) continue;
            into.Add(new ProbeRequest { Kind = ProbeKind.Ping, AdapterId = AdapterId, Family = Family, Target = t2, TimeoutMs = TimeoutMs });
        }
    }

    public override TriState Evaluate(ConditionContext ctx)
    {
        if (ctx.IsAdapterSuppressed(AdapterId)) return TriState.Unknown;
        var adapter = ctx.Snapshot.GetAdapter(AdapterId);
        if (adapter == null) return TriState.Unknown;
        var src = adapter.GetGlobalAddress(Family);
        if (src == null) return TriState.Unknown; // 无源地址：与 PingFail 一致按 Unknown 处理

        var states = new List<TriState>();
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue; // 空目标不参与判定
            var key = new ProbeRequest { Kind = ProbeKind.Ping, AdapterId = AdapterId, Family = Family, Target = t.Trim(), TimeoutMs = TimeoutMs }.Key;
            states.Add(ctx.Snapshot.Results.TryGetValue(key, out var r) ? r.State : TriState.Unknown);
        }
        if (states.Count == 0) return TriState.Unknown;

        if (Mode == PingAggregateMode.All)
        {
            if (states.All(s => s == TriState.True)) return TriState.True;
            if (states.Any(s => s == TriState.False)) return TriState.False;
            return TriState.Unknown;
        }
        else
        {
            if (states.Any(s => s == TriState.True)) return TriState.True;
            if (states.All(s => s == TriState.False)) return TriState.False;
            return TriState.Unknown;
        }
    }

    public override string Describe()
        => $"{Family} ping 成功: {string.Join(" / ", Targets)} ({(Mode == PingAggregateMode.All ? "全部" : "任一")})";
}

/// <summary>网卡处于 Up 状态。</summary>
public sealed class AdapterUpCondition : ConditionBase
{
    public string AdapterId { get; set; } = "";

    public override void CollectProbeRequests(ICollection<ProbeRequest> into)
        => into.Add(new ProbeRequest { Kind = ProbeKind.AdapterUp, AdapterId = AdapterId });

    public override TriState Evaluate(ConditionContext ctx)
    {
        if (ctx.IsAdapterSuppressed(AdapterId)) return TriState.Unknown;
        var adapter = ctx.Snapshot.GetAdapter(AdapterId);
        if (adapter == null) return TriState.Unknown;
        return adapter.Up ? TriState.True : TriState.False;
    }

    public override string Describe() => $"网卡 Up";
}

/// <summary>网卡拥有指定地址族的全局单播地址。</summary>
public sealed class AdapterHasAddressCondition : ConditionBase
{
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv6;

    public override void CollectProbeRequests(ICollection<ProbeRequest> into)
        => into.Add(new ProbeRequest { Kind = ProbeKind.HasGlobalAddress, AdapterId = AdapterId, Family = Family });

    public override TriState Evaluate(ConditionContext ctx)
    {
        if (ctx.IsAdapterSuppressed(AdapterId)) return TriState.Unknown;
        var adapter = ctx.Snapshot.GetAdapter(AdapterId);
        if (adapter == null) return TriState.Unknown;
        return adapter.GetGlobalAddress(Family) != null ? TriState.True : TriState.False;
    }

    public override string Describe() => $"网卡有 {Family} 全局地址";
}

/// <summary>网络配置文件中存在指定名称的配置文件。</summary>
public sealed class ProfileExistsCondition : ConditionBase
{
    public string Name { get; set; } = "";

    public override void CollectProbeRequests(ICollection<ProbeRequest> into) { }

    public override TriState Evaluate(ConditionContext ctx)
    {
        try
        {
            var profiles = ctx.Profiles.GetProfiles();
            bool exists = profiles.Any(p => string.Equals(p.Name, Name, StringComparison.OrdinalIgnoreCase));
            return exists ? TriState.True : TriState.False;
        }
        catch { return TriState.Unknown; }
    }

    public override string Describe() => $"存在配置文件「{Name}」";
}

/// <summary>newerName 的最后连接时间不早于 olderName（且两者都存在）。</summary>
public sealed class ProfileLastConnectedAfterCondition : ConditionBase
{
    public string NewerName { get; set; } = "";
    public string OlderName { get; set; } = "";

    public override void CollectProbeRequests(ICollection<ProbeRequest> into) { }

    public override TriState Evaluate(ConditionContext ctx)
    {
        try
        {
            var profiles = ctx.Profiles.GetProfiles();
            var newer = profiles.FirstOrDefault(p => string.Equals(p.Name, NewerName, StringComparison.OrdinalIgnoreCase));
            var older = profiles.FirstOrDefault(p => string.Equals(p.Name, OlderName, StringComparison.OrdinalIgnoreCase));
            if (newer == null || older == null) return TriState.Unknown;
            if (newer.DateLastConnected == null || older.DateLastConnected == null) return TriState.Unknown;
            return newer.DateLastConnected >= older.DateLastConnected ? TriState.True : TriState.False;
        }
        catch { return TriState.Unknown; }
    }

    public override string Describe() => $"「{NewerName}」最后连接晚于「{OlderName}」";
}

/// <summary>条件组：AND（全部满足）/ OR（任一满足）。</summary>
public sealed class ConditionGroup : ConditionBase
{
    public LogicOperator Operator { get; set; } = LogicOperator.And;
    public List<ConditionBase> Children { get; set; } = new();

    public override void CollectProbeRequests(ICollection<ProbeRequest> into)
    {
        foreach (var c in Children) c.CollectProbeRequests(into);
    }

    public override TriState Evaluate(ConditionContext ctx)
    {
        if (Children.Count == 0) return TriState.Unknown;
        var states = Children.Select(c => SafeEval(c, ctx)).ToList();

        if (Operator == LogicOperator.And)
        {
            if (states.Any(s => s == TriState.False)) return TriState.False;
            if (states.All(s => s == TriState.True)) return TriState.True;
            return TriState.Unknown;
        }
        else
        {
            if (states.Any(s => s == TriState.True)) return TriState.True;
            if (states.All(s => s == TriState.False)) return TriState.False;
            return TriState.Unknown;
        }
    }

    private static TriState SafeEval(ConditionBase c, ConditionContext ctx)
    {
        try { return c.Evaluate(ctx); }
        catch { return TriState.Unknown; }
    }

    public override string Describe()
    {
        string op = Operator == LogicOperator.And ? "且" : "或";
        return string.Join($" {op} ", Children.Select(c => c.Describe()));
    }
}
