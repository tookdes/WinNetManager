namespace WinNetManager.Interop;

public enum NLM_ENUM_NETWORK
{
    NLM_ENUM_NETWORK_CONNECTED = 0x01,
    NLM_ENUM_NETWORK_DISCONNECTED = 0x02,
    NLM_ENUM_NETWORK_ALL = 0x03
}

public enum NLM_NETWORK_CATEGORY
{
    NLM_NETWORK_CATEGORY_PUBLIC = 0x00,
    NLM_NETWORK_CATEGORY_PRIVATE = 0x01,
    NLM_NETWORK_CATEGORY_DOMAIN_AUTHENTICATED = 0x02
}

/// <summary>
/// CLSID / helper constants for late-bound COM access to INetworkListManager.
/// Using dynamic COM avoids vtable layout issues with IDispatch-derived interfaces.
/// </summary>
public static class NetworkListManagerCom
{
    public static readonly Guid CLSID_NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public static dynamic CreateInstance()
    {
        var type = Type.GetTypeFromCLSID(CLSID_NetworkListManager)
            ?? throw new InvalidOperationException("NetworkListManager COM class not found");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create NetworkListManager instance");
    }
}
