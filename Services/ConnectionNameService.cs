using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinNetManager.Models;

namespace WinNetManager.Services;

public static class ConnectionNameService
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    public static event Action? ConnectionsChanged;

    internal static void RaiseConnectionsChanged() => ConnectionsChanged?.Invoke();

    public static List<ConnectionInfo> GetAllConnections()
    {
        var connections = new List<ConnectionInfo>();
        var activeGuids = GetActiveAdapterGuids();

        using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
        if (classKey == null) return connections;

        foreach (string subName in classKey.GetSubKeyNames())
        {
            if (!Guid.TryParse(subName, out Guid guid)) continue;

            using var connKey = classKey.OpenSubKey($@"{subName}\Connection");
            if (connKey == null) continue;

            string name = connKey.GetValue("Name") as string ?? "";
            string pnpId = connKey.GetValue("PnpInstanceID") as string ?? "";

            connections.Add(new ConnectionInfo
            {
                Guid = guid,
                Name = name,
                PnpInstanceId = pnpId,
                HasActiveAdapter = activeGuids.Contains(guid.ToString("B"))
            });
        }

        connections.Sort((a, b) => NaturalStringCompare(a.Name, b.Name));
        return connections;
    }

    /// <summary>
    /// 重命名网络连接。
    /// 必须使用 Rename-NetAdapter 而非直接写注册表：写注册表只改了存储值，
    /// 网络栈（Get-NetIPInterface / Get-DnsClientServerAddress 等读取的 InterfaceAlias）
    /// 不会感知变更，导致其他标签页继续显示旧名称。
    /// Rename-NetAdapter 会通知网络栈同步更新 InterfaceAlias。
    /// 另外 Get-NetAdapter 不支持 -InterfaceGuid 参数，需先读旧名称按名查找。
    /// </summary>
    public static void RenameConnection(Guid guid, string newName)
    {
        string keyPath = $@"{NetworkClassKeyPath}\{guid:B}\Connection";

        // 读取旧名称，用于通过 PowerShell 按名查找适配器
        string oldName = "";
        using (var readKey = Registry.LocalMachine.OpenSubKey(keyPath))
            oldName = readKey?.GetValue("Name") as string ?? "";

        // 优先用 Rename-NetAdapter，它会正确通知网络栈更新 InterfaceAlias
        if (!string.IsNullOrEmpty(oldName))
        {
            string safeOld = ProcessRunner.EscapePsSingleQuoted(oldName);
            string safeNew = ProcessRunner.EscapePsSingleQuoted(newName);
            string script =
                $"Get-NetAdapter -Name '{safeOld}' -ErrorAction SilentlyContinue | " +
                $"Rename-NetAdapter -NewName '{safeNew}'";
            string error;
            ProcessRunner.RunPowerShell(script, out error, 15000);

            bool success = string.IsNullOrEmpty(error)
                || error.IndexOf("警告", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0;

            if (success) return;
        }

        // 回退：直接写注册表（网络栈不会立即感知，但下次重启后生效）
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.SetValue("Name", newName);
    }

    public static void DeleteConnection(Guid guid)
    {
        using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath, writable: true);
        classKey?.DeleteSubKeyTree(guid.ToString("B"), throwOnMissingSubKey: false);
    }

    private static HashSet<string> GetActiveAdapterGuids()
    {
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // NetworkInterface.Id is the adapter GUID, e.g. "{E33F5A1A-...}"
                string id = nic.Id;
                // Normalize to {GUID} format
                if (Guid.TryParse(id, out Guid g))
                    guids.Add(g.ToString("B"));
                else
                    guids.Add(id);
            }
        }
        catch { }
        return guids;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    private static int NaturalStringCompare(string a, string b)
    {
        return StrCmpLogicalW(a ?? "", b ?? "");
    }
}
