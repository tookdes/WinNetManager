using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinNetManager.Models;

namespace WinNetManager.Services;

public static class ConnectionNameService
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

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

    public static void RenameConnection(Guid guid, string newName)
    {
        string keyPath = $@"{NetworkClassKeyPath}\{guid:B}\Connection";
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
