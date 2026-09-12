
using System.Text.Json.Serialization;
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RenewAdapterAction), "renewAdapter")]
[JsonDerivedType(typeof(RestartAdapterAction), "restartAdapter")]
[JsonDerivedType(typeof(HttpRequestAction), "httpRequest")]
[JsonDerivedType(typeof(NotifyAction), "notify")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(WaitUntilAdapterHasAddressAction), "waitUntilAddress")]
[JsonDerivedType(typeof(DeleteNetworkProfileAction), "deleteProfile")]
[JsonDerivedType(typeof(RenameNetworkProfileAction), "renameProfile")]
[JsonDerivedType(typeof(MergeNetworkProfilesAction), "mergeProfiles")]
public abstract class ActionBase
{
    public OnFailureMode OnFailure { get; set; } = OnFailureMode.Continue;
    public RunWhenMode RunWhen { get; set; } = RunWhenMode.Always;

    public abstract Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct);
    public abstract string Describe();
    public abstract string? BuildCommandPreview(ActionContext ctx);
}

/// <summary>轻量软刷新：ipconfig /renew（IPv4）或 /renew6（IPv6）。</summary>
public sealed class RenewAdapterAction : ActionBase
{
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv6;

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var adapter = ctx.Adapters.GetAdapter(AdapterId);
        if (adapter == null)
            return Task.FromResult(new ActionResult { Ok = false, Message = "网卡不存在，无法 Renew。" });

        string sw = Family == AddressFamilyKind.IPv6 ? "/renew6" : "/renew";
        string cmd = Family == AddressFamilyKind.IPv6 ? $"ipconfig /renew6 \"{adapter.Name}\"" : $"ipconfig /renew \"{adapter.Name}\"";
        string error;
        string output = ctx.Shell.Run("ipconfig.exe", new[] { sw, adapter.Name }, out error, out int exitCode, 60000);

        // 只看退出码："媒体已断开/无 DHCP" 等输出表示没有成功续租，不能当成功处理
        bool ok = exitCode == 0;
        return Task.FromResult(new ActionResult
        {
            Ok = ok,
            Message = ok ? $"{Family} Renew 已执行（{adapter.Name}）。" : (Trim(error) + Trim(output)),
            CommandPreview = cmd,
            SuppressAdapterIds = new[] { AdapterId },
            SuppressDurationSeconds = 15,
        });
    }

    private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim() + "\n";

    public override string Describe() => $"Renew {Family}（网卡）";

    public override string? BuildCommandPreview(ActionContext ctx)
    {
        var adapter = ctx.Adapters.GetAdapter(AdapterId);
        if (adapter == null) return null;
        return Family == AddressFamilyKind.IPv6
            ? $"ipconfig /renew6 \"{adapter.Name}\""
            : $"ipconfig /renew \"{adapter.Name}\"";
    }
}

/// <summary>物理重启网卡（Restart-NetAdapter）。破坏性动作。</summary>
public sealed class RestartAdapterAction : ActionBase
{
    public string AdapterId { get; set; } = "";

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var adapter = ctx.Adapters.GetAdapter(AdapterId);
        if (adapter == null)
            return Task.FromResult(new ActionResult { Ok = false, Message = "网卡不存在，无法重启。" });

        string script = $"Restart-NetAdapter -Name '{ProcessRunnerCompat.EscapePs(adapter.Name)}' -Confirm:$false";
        string error;
        ctx.Shell.RunPowerShell(script, out error, 60000);

        bool ok = string.IsNullOrWhiteSpace(error)
            || error.Contains("警告", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Warning", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new ActionResult
        {
            Ok = ok,
            Message = ok ? $"网卡「{adapter.Name}」重启命令已提交。" : error.Trim(),
            IsDestructive = true,
            CommandPreview = script,
            SuppressAdapterIds = new[] { AdapterId },
            SuppressDurationSeconds = 120,
        });
    }

    public override string Describe() => "重启网卡";

    public override string? BuildCommandPreview(ActionContext ctx)
    {
        var adapter = ctx.Adapters.GetAdapter(AdapterId);
        if (adapter == null) return null;
        return $"Restart-NetAdapter -Name '{ProcessRunnerCompat.EscapePs(adapter.Name)}' -Confirm:$false";
    }
}

/// <summary>HTTP 请求（curl.exe）：可绑定指定网卡源地址，或走 socks5h 代理。</summary>
public sealed class HttpRequestAction : ActionBase
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string? ViaAdapterId { get; set; }
    public AddressFamilyKind ViaAdapterFamily { get; set; } = AddressFamilyKind.IPv4;
    public string? SocksProxy { get; set; }
    public string? ResolveHost { get; set; }
    public string? ResolveIp { get; set; }
    public int? ExpectedStatus { get; set; }
    public int TimeoutMs { get; set; } = 10000;

    public override async Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var args = BuildArgs(ctx);
        if (args == null)
            return new ActionResult { Ok = false, Message = "指定出口网卡无可用地址，无法绑定发送。" };

        string error = "";
        int exitCode = -2;
        string output = "";
        await Task.Run(() => { output = ctx.Shell.Run("curl.exe", args, out error, out exitCode, TimeoutMs + 5000); }, ct);
        bool parsed = int.TryParse(output.Trim(), out int status);
        bool ok = exitCode == 0 && parsed && (ExpectedStatus == null || status == ExpectedStatus.Value);
        string msg = ok
            ? $"HTTP {status}：{Redactor.Redact(Url)}"
            : $"curl 退出码 {exitCode}，状态 {output.Trim()}{(string.IsNullOrWhiteSpace(error) ? "" : "，err=" + error.Trim())}";
        return new ActionResult
        {
            Ok = ok,
            Message = msg,
            CommandPreview = "curl " + string.Join(" ", args.Select(Redactor.RedactArg)),
        };
    }

    /// <summary>构建 curl 参数（ExecuteAsync 与 BuildCommandPreview 共用，保证预览与实际一致）。</summary>
    private List<string>? BuildArgs(ActionContext ctx)
    {
        var args = new List<string> { "-sS", "-o", "NUL", "-w", "%{http_code}", "--connect-timeout", "5", "--max-time", (TimeoutMs / 1000).ToString() };
        if (!string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-X"); args.Add(Method.ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(ViaAdapterId))
        {
            var adapter = ctx.Adapters.GetAdapter(ViaAdapterId!);
            var viaIp = adapter?.GetGlobalAddress(ViaAdapterFamily);
            if (viaIp == null) return null;
            args.Add("--interface"); args.Add(viaIp);
        }
        if (!string.IsNullOrWhiteSpace(SocksProxy))
        {
            args.Add("--proxy"); args.Add($"socks5h://{SocksProxy}");
        }
        if (!string.IsNullOrWhiteSpace(ResolveHost) && !string.IsNullOrWhiteSpace(ResolveIp))
        {
            string? resolve = BuildResolveArg();
            if (resolve != null)
            {
                args.Add("--resolve");
                args.Add(resolve);
            }
        }
        args.Add(Url);
        return args;
    }

    /// <summary>
    /// 生成 curl --resolve 的 HOST:PORT:ADDRESS。
    /// 端口优先取 URL 中的端口（默认 80/443）；ResolveHost 可带 :port 或 IPv6 方括号。
    /// </summary>
    private string? BuildResolveArg()
    {
        string host = ResolveHost!.Trim();
        int port = 443;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            port = uri.Port;
            if (uri.Scheme == Uri.UriSchemeHttp && uri.IsDefaultPort) port = 80;
        }

        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close > 0)
            {
                string inner = host[1..close];
                string rest = host[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p)) port = p;
                host = inner;
            }
        }
        else
        {
            // 非方括号的 host:port（IPv4 主机名场景）
            int lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && host.IndexOf(':') == lastColon)
            {
                if (int.TryParse(host[(lastColon + 1)..], out int p)) port = p;
                host = host[..lastColon];
            }
        }

        string hostPart = host.Contains(':') ? $"[{host}]" : host;
        string ipPart = ResolveIp!.Trim();
        if (ipPart.Contains(':')) ipPart = $"[{ipPart}]";
        return $"{hostPart}:{port}:{ipPart}";
    }

    public override string Describe()
    {
        string via = string.IsNullOrWhiteSpace(ViaAdapterId) ? (string.IsNullOrWhiteSpace(SocksProxy) ? "默认路由" : $"socks5h://{Redactor.Redact(SocksProxy!)}") : $"经网卡 {ViaAdapterId}";
        return $"{Method.ToUpperInvariant()} {Redactor.Redact(Url)}（{via}）";
    }

    public override string? BuildCommandPreview(ActionContext ctx)
    {
        var args = BuildArgs(ctx);
        if (args == null) return null;
        return string.Join(" ", args.Select(Redactor.RedactArg));
    }
}

/// <summary>应用内通知：UI 日志 + 状态栏 + 可选弹窗。</summary>
public sealed class NotifyAction : ActionBase
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Popup { get; set; }

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
        => Task.FromResult(new ActionResult { Ok = true, Message = $"通知：{Title} {Message}" });

    public override string Describe() => $"通知「{Title}」";

    public override string? BuildCommandPreview(ActionContext ctx) => null;
}

/// <summary>等待固定秒数（动作链内的时序原语）。</summary>
public sealed class WaitAction : ActionBase
{
    public int Seconds { get; set; } = 30;

    public override async Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Seconds)), ct);
        return new ActionResult { Ok = true, Message = $"等待 {Seconds} 秒。" };
    }

    public override string Describe() => $"等待 {Seconds} 秒";

    public override string? BuildCommandPreview(ActionContext ctx) => null;
}

/// <summary>等待网卡重新获得指定族的全局地址（网络副作用动作的验证阶段）。</summary>
public sealed class WaitUntilAdapterHasAddressAction : ActionBase
{
    public string AdapterId { get; set; } = "";
    public AddressFamilyKind Family { get; set; } = AddressFamilyKind.IPv6;
    public int TimeoutSeconds { get; set; } = 120;

    public override async Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddSeconds(TimeoutSeconds);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var adapter = ctx.Adapters.GetAdapter(AdapterId);
            if (adapter?.GetGlobalAddress(Family) != null)
                return new ActionResult { Ok = true, Message = $"网卡已获得 {Family} 全局地址。" };
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return new ActionResult { Ok = false, Message = $"等待 {TimeoutSeconds} 秒后网卡仍未获得 {Family} 全局地址（ExecutedButNotRecovered）。" };
    }

    public override string Describe() => $"等待网卡恢复 {Family} 地址（最多 {TimeoutSeconds} 秒）";

    public override string? BuildCommandPreview(ActionContext ctx) => null;
}

/// <summary>删除网络配置文件（按名称唯一命中；当前已连接拒绝）。破坏性动作。</summary>
public sealed class DeleteNetworkProfileAction : ActionBase
{
    public string Name { get; set; } = "";

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        bool ok = ctx.ProfileMutator.DeleteProfileByName(Name, out string error);
        return Task.FromResult(new ActionResult { Ok = ok, Message = ok ? $"已删除配置文件「{Name}」。" : error, IsDestructive = true });
    }

    public override string Describe() => $"删除配置文件「{Name}」";

    public override string? BuildCommandPreview(ActionContext ctx) => $"删除网络配置文件「{Name}」（注册表 + 签名清理）";
}

/// <summary>重命名网络配置文件。破坏性动作。</summary>
public sealed class RenameNetworkProfileAction : ActionBase
{
    public string FromName { get; set; } = "";
    public string ToName { get; set; } = "";

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        bool ok = ctx.ProfileMutator.RenameProfileByName(FromName, ToName, out string error);
        return Task.FromResult(new ActionResult { Ok = ok, Message = ok ? $"已重命名「{FromName}」→「{ToName}」。" : error, IsDestructive = true });
    }

    public override string Describe() => $"重命名「{FromName}」→「{ToName}」";

    public override string? BuildCommandPreview(ActionContext ctx) => $"重命名网络配置文件「{FromName}」→「{ToName}」";
}

/// <summary>
/// 受保护的复合动作：合并重复的网络配置文件（如「网络 2」→ 删除旧「网络」并改名）。
/// 执行前完整预检，先备份再操作，删除/重命名按名称唯一命中且当前连接保护。
/// </summary>
public sealed class MergeNetworkProfilesAction : ActionBase
{
    public string KeepName { get; set; } = "";
    public string DuplicateName { get; set; } = "";

    public override Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var profiles = ctx.Profiles.GetProfiles();
        var keep = profiles.Where(p => string.Equals(p.Name, KeepName, StringComparison.OrdinalIgnoreCase)).ToList();
        var dup = profiles.Where(p => string.Equals(p.Name, DuplicateName, StringComparison.OrdinalIgnoreCase)).ToList();

        // 预检
        if (keep.Count != 1) return Task.FromResult(Fail($"预检失败：要保留的「{KeepName}」匹配 {keep.Count} 个配置文件（需恰好 1 个）。"));
        if (dup.Count != 1) return Task.FromResult(Fail($"预检失败：重复项「{DuplicateName}」匹配 {dup.Count} 个配置文件（需恰好 1 个）。"));
        // 典型场景是「网络 2」刚连上（当前连接）、旧「网络」是历史项：
        // 重命名当前连接的 Profile 是安全的（只改注册表 ProfileName），所以允许 dup 为当前连接；
        // 但删除当前连接的 Profile 会断网，keep 为当前连接时拒绝。
        if (keep[0].IsConnected) return Task.FromResult(Fail($"预检失败：要保留的「{KeepName}」当前已连接，禁止自动删除。"));
        // 时间比较回退到 DateCreated（DateLastConnected 可能为 null，如从未连接过的 NLA 新条目）
        var dupTime = dup[0].DateLastConnected ?? dup[0].DateCreated;
        var keepTime = keep[0].DateLastConnected ?? keep[0].DateCreated;
        if (dupTime != null && keepTime != null && dupTime < keepTime)
            return Task.FromResult(Fail($"预检失败：「{DuplicateName}」的最后连接时间早于「{KeepName}」，不满足合并条件。"));
        if (!LooksLikeNumberedNetworkName(DuplicateName))
            return Task.FromResult(Fail($"预检失败：「{DuplicateName}」不是形如「网络 2」/「Network 2」的名称，拒绝操作。"));
        DateTime? created = dup[0].DateCreated ?? dup[0].DateLastConnected;
        if (created != null && (DateTime.Now - created.Value).TotalMinutes < 10)
            return Task.FromResult(Fail($"预检失败：「{DuplicateName}」创建/连接不足 10 分钟，等待 NLA 稳定后再处理。"));

        // 备份
        if (!ctx.ProfileMutator.BackupProfiles(out string backupPath))
            return Task.FromResult(Fail("预检失败：网络配置文件注册表备份失败，已中止。"));

        // 执行：先删 keep，再重命名 dup -> keep
        if (!ctx.ProfileMutator.DeleteProfileByName(KeepName, out string delErr))
            return Task.FromResult(Fail($"删除「{KeepName}」失败：{delErr}（备份：{backupPath}）"));

        bool renamed = ctx.ProfileMutator.RenameProfileByName(DuplicateName, KeepName, out string renErr);
        if (!renamed)
        {
            sb.AppendLine($"部分成功：「{KeepName}」已删除，但重命名「{DuplicateName}」→「{KeepName}」失败：{renErr}");
            sb.AppendLine($"人工恢复：备份位于 {backupPath}，或手动将「{DuplicateName}」重命名为「{KeepName}」。");
            return Task.FromResult(new ActionResult { Ok = false, Message = sb.ToString().Trim(), IsDestructive = true });
        }

        sb.AppendLine($"已合并：「{DuplicateName}」→「{KeepName}」，旧「{KeepName}」已删除。");
        sb.AppendLine($"备份：{backupPath}");
        return Task.FromResult(new ActionResult { Ok = true, Message = sb.ToString().Trim(), IsDestructive = true });
    }

    // 预检失败不视为破坏性动作：不应计入全局熔断（否则三次预检失败会误熔断所有破坏性动作）
    private static ActionResult Fail(string msg) => new() { Ok = false, Message = msg, IsDestructive = false };

    private static bool LooksLikeNumberedNetworkName(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(网络|Network)\s*\d+$");

    public override string Describe() => $"合并「{DuplicateName}」→「{KeepName}」";

    public override string? BuildCommandPreview(ActionContext ctx)
        => $"备份 NetworkList → 删除「{KeepName}」→ 重命名「{DuplicateName}」为「{KeepName}」（预检通过后执行）";
}

/// <summary>ProcessRunner 兼容转义（PowerShell 单引号转义）。</summary>
public static class ProcessRunnerCompat
{
    public static string EscapePs(string input) => input.Replace("'", "''");
}
