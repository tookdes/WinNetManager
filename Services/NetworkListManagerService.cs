using WinNetManager.Interop;
using WinNetManager.Models;

namespace WinNetManager.Services;

/// <summary>
/// Wraps COM INetworkListManager via late-bound dynamic COM for reliability.
/// All COM calls must happen on an STA thread (WPF UI thread is STA).
/// </summary>
public static class NetworkListManagerService
{
    public static HashSet<Guid> GetConnectedNetworkIds()
    {
        var result = new HashSet<Guid>();
        try
        {
            dynamic nlm = NetworkListManagerCom.CreateInstance();
            foreach (dynamic network in nlm.GetNetworks((int)NLM_ENUM_NETWORK.NLM_ENUM_NETWORK_CONNECTED))
            {
                try
                {
                    Guid id = new(network.GetNetworkId().ToString());
                    result.Add(id);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    public static void SetCategoryForConnectedNetwork(Guid networkId, NetworkCategory category)
    {
        dynamic nlm = NetworkListManagerCom.CreateInstance();
        dynamic network = nlm.GetNetwork(networkId);
        network.SetCategory((int)(NLM_NETWORK_CATEGORY)(int)category);
    }

    public static List<(Guid NetworkId, string Name, Guid AdapterId)> GetConnectedNetworkDetails()
    {
        var result = new List<(Guid, string, Guid)>();
        try
        {
            dynamic nlm = NetworkListManagerCom.CreateInstance();
            foreach (dynamic conn in nlm.GetNetworkConnections())
            {
                try
                {
                    dynamic net = conn.GetNetwork();
                    string name = net.GetName();
                    Guid netId = new(net.GetNetworkId().ToString());
                    Guid adapterId = new(conn.GetAdapterId().ToString());
                    result.Add((netId, name, adapterId));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }
}
