
using System.IO;
using System.Net.NetworkInformation;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Engine;
using WinNetManager.Core.Probing;
using WinNetManager.Core.Storage;

namespace WinNetManager.Services.Automation;

/// <summary>
/// 引擎生产组装：单例，负责创建引擎并挂接系统网络变更监听。
/// </summary>
public static class AutomationHost
{
    public static AutomationEngine Engine { get; private set; } = null!;
    /// <summary>规则存储单例：LastLoadFailed（坏文件保护）等实例状态在 UI/引擎间共享。</summary>
    public static JsonRuleStore RuleStore { get; private set; } = null!;
    /// <summary>持久审计日志；UI 用它恢复跨进程的最近运行记录。</summary>
    public static JsonlAuditLog AuditLog { get; private set; } = null!;

    public static void Initialize()
    {
        if (Engine != null) return;
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinNetManager");

        var shell = new ShellAdapter();
        var probes = new NetworkProbeManager(shell);
        RuleStore = new JsonRuleStore(Path.Combine(appData, "automation_rules.json"));
        AuditLog = new JsonlAuditLog(Path.Combine(appData, "audit"));
        Engine = new AutomationEngine(
            SystemClock.Instance,
            probes,
            new ProfileProviderAdapter(),
            new ProfileMutatorAdapter(),
            probes,
            shell,
            RuleStore,
            new JsonRuntimeStateStore(Path.Combine(appData, "automation_state.json")),
            AuditLog);

        // 网络变更/唤醒后进入稳定等待期，避免抖动误触发
        NetworkChange.NetworkAddressChanged += (_, _) => Engine.NotifyNetworkChanged();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Engine.NotifyNetworkChanged();
    }
}
