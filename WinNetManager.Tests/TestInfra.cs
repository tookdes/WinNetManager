
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Engine;
using WinNetManager.Core.Models;
using WinNetManager.Core.Probing;
using WinNetManager.Core.Storage;

namespace WinNetManager.Tests;

/// <summary>可手动推进的虚拟时钟。</summary>
public sealed class VirtualClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
    public DateTime UtcNow => Now.ToUniversalTime();
    public void Advance(TimeSpan t) => Now = Now.Add(t);
    public void AdvanceMinutes(int m) => Now = Now.AddMinutes(m);
}

/// <summary>可编程探针：按 ProbeRequest.Key 返回预设三态。</summary>
public sealed class FakeProbeSource : IProbeSource, IAdapterLocator
{
    public Dictionary<string, TriState> Results { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AdapterSnapshot> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<ProbeRequest, TriState>? Resolver { get; set; }

    public void SetPing(string adapterId, AddressFamilyKind family, string target, TriState state)
        => Results[new ProbeRequest { Kind = ProbeKind.Ping, AdapterId = adapterId, Family = family, Target = target }.Key] = state;

    public Task<ProbeSnapshot> ProbeAsync(IReadOnlyCollection<ProbeRequest> requests, CancellationToken ct)
    {
        var snap = new ProbeSnapshot { Timestamp = DateTime.Now };
        foreach (var (k, a) in Adapters) snap.Adapters[k] = a;
        foreach (var r in requests)
        {
            TriState st;
            if (Results.TryGetValue(r.Key, out var v)) st = v;
            else if (Resolver != null) st = Resolver(r);
            else st = TriState.Unknown;
            snap.Results[r.Key] = new ProbeResult { State = st, Reason = "fake" };
        }
        return Task.FromResult(snap);
    }

    public IReadOnlyList<AdapterSnapshot> GetAdapters() => Adapters.Values.ToList();
    public AdapterSnapshot? GetAdapter(string adapterId)
        => Adapters.TryGetValue(adapterId, out var a) ? a : null;
}

/// <summary>记录命令的假 Shell。</summary>
public sealed class FakeShell : IShell
{
    public List<string> PowerShellScripts { get; } = new();
    public List<string[]> ExeCalls { get; } = new();
    public int PingExitCode { get; set; } = 0;
    public string PingOutput { get; set; } = "";

    public string Run(string fileName, IEnumerable<string> arguments, out string error, out int exitCode, int timeoutMs)
    {
        ExeCalls.Add(arguments.ToArray());
        error = "";
        exitCode = 0;
        return "";
    }

    public string RunPowerShell(string script, out string error, out int exitCode, int timeoutMs)
    {
        PowerShellScripts.Add(script);
        error = "";
        exitCode = 0;
        return "";
    }

    public string RunPowerShell(string script, out string error, int timeoutMs)
    {
        PowerShellScripts.Add(script);
        error = "";
        return "";
    }
}

/// <summary>可编程配置文件提供者。</summary>
public sealed class FakeProfileProvider : IProfileProvider
{
    public List<ProfileInfo> Profiles { get; } = new();
    public IReadOnlyList<ProfileInfo> GetProfiles() => Profiles;
}

/// <summary>可编程配置文件修改器。</summary>
public sealed class FakeProfileMutator : IProfileMutator
{
    public bool DeleteResult { get; set; } = true;
    public bool RenameResult { get; set; } = true;
    public bool BackupResult { get; set; } = true;
    public string DeleteError { get; set; } = "";
    public string RenameError { get; set; } = "";
    public List<string> Deleted { get; } = new();
    public List<(string From, string To)> Renamed { get; } = new();
    public int BackupCalls { get; set; }

    public bool BackupProfiles(out string backupPath)
    {
        BackupCalls++;
        backupPath = "C:\\fake\\backup.reg";
        return BackupResult;
    }
    public bool DeleteProfileByName(string name, out string error)
    {
        error = DeleteError;
        if (DeleteResult) Deleted.Add(name);
        return DeleteResult;
    }
    public bool RenameProfileByName(string fromName, string toName, out string error)
    {
        error = RenameError;
        if (RenameResult) Renamed.Add((fromName, toName));
        return RenameResult;
    }
}

public sealed class MemoryRuleStore : IRuleStore
{
    public List<AutomationRule> Rules { get; set; } = new();
    public List<AutomationRule> Load() => Rules;
    public void Save(IEnumerable<AutomationRule> rules) => Rules = rules.ToList();
}

public sealed class MemoryStateStore : IRuntimeStateStore
{
    public Dictionary<string, RuleRuntimeState> States { get; set; } = new();
    public Dictionary<string, RuleRuntimeState> Load() => States;
    public void Save(IReadOnlyDictionary<string, RuleRuntimeState> states) => States = states.ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>内存审计（测试用）。</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new();
    public void Write(AuditEntry entry) => Entries.Add(entry);
}

/// <summary>测试夹具：组装引擎 + 全部 Fake 依赖。</summary>
public sealed class EngineFixture : IDisposable
{
    public VirtualClock Clock { get; } = new();
    public FakeProbeSource Probes { get; } = new();
    public FakeProfileProvider Profiles { get; } = new();
    public FakeProfileMutator ProfileMutator { get; } = new();
    public FakeShell Shell { get; } = new();
    public MemoryRuleStore RuleStore { get; } = new();
    public MemoryStateStore StateStore { get; } = new();
    public InMemoryAuditLog Audit { get; } = new();
    public AutomationEngine Engine { get; }

    public EngineFixture(int suppressionSeconds = 120)
    {
        Engine = new AutomationEngine(Clock, Probes, Profiles, ProfileMutator, Probes, Shell,
            RuleStore, StateStore, Audit, new EngineOptions
            {
                StartupGraceSeconds = 0,
                NetworkChangeSettleSeconds = 0,
                GlobalCircuitWindowMinutes = 10,
                GlobalCircuitMaxDestructive = 3,
                SuppressionSeconds = suppressionSeconds,
                TickSeconds = 1,
            });
        Engine.GlobalEnabled = true;
        // 不 Start()：测试直接驱动 TickAsync，避免后台循环干扰
    }

    public AdapterSnapshot AddAdapter(string id, string name, string? ipv4, string? ipv6)
    {
        var a = new AdapterSnapshot { Id = id, Name = name, Up = true, GlobalIpv4 = ipv4, GlobalIpv6 = ipv6 };
        Probes.Adapters[id] = a;
        return a;
    }

    public AutomationRule AddPingRule(string adapterId, string target, int consecutive = 3, int sustain = 0, bool dryRun = true)
    {
        var rule = new AutomationRule
        {
            Name = "测试规则",
            Enabled = true,
            DryRun = dryRun,
            IntervalMinutes = 1,
            ConsecutiveMatches = consecutive,
            SustainSeconds = sustain,
            PreActionDelaySeconds = 0,
            CooldownSeconds = 0,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new PingFailCondition { AdapterId = adapterId, Family = AddressFamilyKind.IPv6, Targets = { target }, TimeoutMs = 3000 },
                }
            },
            Actions = { new NotifyAction { Title = "t", Message = "m" } },
        };
        RuleStore.Rules.Add(rule);
        return rule;
    }

    public async Task TickAsync() => await Engine.TickAsync(CancellationToken.None);

    public void Dispose() => Engine.Dispose();
}
