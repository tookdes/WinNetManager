using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinNetManager.Services;

internal sealed class RouteSelectionInfo
{
    public bool Success { get; init; }
    public string LookupStage { get; init; } = "";
    public int ErrorCode { get; init; }
    public string Error { get; init; } = "";

    public string Destination { get; init; } = "";
    public string AddressFamily { get; init; } = "";
    public string BestSourceAddress { get; init; } = "";
    public uint InterfaceIndex { get; init; }
    public string InterfaceName { get; init; } = "";
    public string InterfaceDescription { get; init; } = "";
    public string NextHop { get; init; } = "";
    public string DestinationPrefix { get; init; } = "";
    public uint RouteMetric { get; init; }
    public uint? InterfaceMetric { get; init; }
    public uint? EffectiveMetric =>
        InterfaceMetric.HasValue ? RouteMetric + InterfaceMetric.Value : null;

    public string InterfaceDisplay =>
        string.IsNullOrWhiteSpace(InterfaceName)
            ? (InterfaceIndex == 0 ? "" : $"ifIndex {InterfaceIndex}")
            : $"{InterfaceName} (ifIndex {InterfaceIndex})";
}

/// <summary>
/// Read-only route attribution using Windows IP Helper APIs.
///
/// GetBestInterfaceEx first asks Windows which interface currently owns the best
/// route to the destination across IPv4/IPv6. GetBestRoute2 is then constrained
/// to that interface to retrieve the best source address, next hop and route
/// metric. GetIpInterfaceEntry supplies the interface metric when available.
///
/// This is route-selection evidence, not packet capture. TCP/HTTP probes should
/// additionally record their actual socket LocalEndPoint/RemoteEndPoint.
/// </summary>
internal static class RouteSelectionService
{
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;
    private const int ScopeLevelCount = 16;

    public static RouteSelectionInfo Lookup(IPAddress destination)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        var nativeDestination = SockaddrInet.FromIPAddress(destination);

        uint error = GetBestInterfaceEx(ref nativeDestination, out uint ifIndex);
        if (error != 0)
            return Failure(destination, "GetBestInterfaceEx", error, ifIndex);

        error = GetBestRoute2(
            IntPtr.Zero,
            ifIndex,
            IntPtr.Zero,
            ref nativeDestination,
            0,
            out MibIpforwardRow2 route,
            out SockaddrInet bestSource);

        if (error != 0)
            return Failure(destination, "GetBestRoute2", error, ifIndex);

        uint? interfaceMetric = TryGetInterfaceMetric(
            nativeDestination.Family,
            ifIndex,
            out string metricError);

        var ni = FindNetworkInterface(ifIndex, destination.AddressFamily);

        IPAddress? nextHop = route.NextHop.ToIPAddress();
        IPAddress? prefix = route.DestinationPrefix.Prefix.ToIPAddress();
        IPAddress? source = bestSource.ToIPAddress();

        string errorText = metricError;
        return new RouteSelectionInfo
        {
            Success = true,
            LookupStage = "GetBestInterfaceEx + GetBestRoute2",
            Error = errorText,
            Destination = destination.ToString(),
            AddressFamily = destination.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6",
            BestSourceAddress = source?.ToString() ?? "",
            InterfaceIndex = route.InterfaceIndex != 0 ? route.InterfaceIndex : ifIndex,
            InterfaceName = ni?.Name ?? "",
            InterfaceDescription = ni?.Description ?? "",
            NextHop = IsUnspecified(nextHop) ? "" : nextHop?.ToString() ?? "",
            DestinationPrefix = prefix == null
                ? ""
                : $"{prefix}/{route.DestinationPrefix.PrefixLength}",
            RouteMetric = route.Metric,
            InterfaceMetric = interfaceMetric
        };
    }

    private static RouteSelectionInfo Failure(
        IPAddress destination,
        string stage,
        uint error,
        uint interfaceIndex)
    {
        return new RouteSelectionInfo
        {
            Success = false,
            LookupStage = stage,
            ErrorCode = unchecked((int)error),
            Error = new Win32Exception(unchecked((int)error)).Message,
            Destination = destination.ToString(),
            AddressFamily = destination.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6",
            InterfaceIndex = interfaceIndex
        };
    }

    private static NetworkInterface? FindNetworkInterface(uint interfaceIndex, AddressFamily family)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                int index = family == AddressFamily.InterNetwork
                    ? props.GetIPv4Properties()?.Index ?? -1
                    : props.GetIPv6Properties()?.Index ?? -1;

                if (index >= 0 && (uint)index == interfaceIndex)
                    return ni;
            }
            catch { }
        }

        return null;
    }

    private static uint? TryGetInterfaceMetric(
        ushort family,
        uint interfaceIndex,
        out string error)
    {
        error = "";
        try
        {
            var row = new MibIpinterfaceRow
            {
                Family = family,
                InterfaceIndex = interfaceIndex,
                ZoneIndices = new uint[ScopeLevelCount]
            };

            uint code = GetIpInterfaceEntry(ref row);
            if (code == 0)
                return row.Metric;

            error = $"GetIpInterfaceEntry failed: {code} ({new Win32Exception(unchecked((int)code)).Message})";
            return null;
        }
        catch (Exception ex)
        {
            // Interface metric is supplemental evidence. A marshalling/API failure here
            // must not discard the already-valid route/source/interface decision.
            error = $"GetIpInterfaceEntry unavailable: {ex.GetBaseException().Message}";
            return null;
        }
    }

    private static bool IsUnspecified(IPAddress? address)
    {
        if (address == null)
            return true;
        return address.Equals(IPAddress.Any) ||
               address.Equals(IPAddress.IPv6Any);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(
        ref SockaddrInet pDestAddr,
        out uint pdwBestIfIndex);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute2(
        IntPtr interfaceLuid,
        uint interfaceIndex,
        IntPtr sourceAddress,
        ref SockaddrInet destinationAddress,
        uint addressSortOptions,
        out MibIpforwardRow2 bestRoute,
        out SockaddrInet bestSourceAddress);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpInterfaceEntry(ref MibIpinterfaceRow row);

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)]
        public ushort Family;

        [FieldOffset(0)]
        public SockaddrIn Ipv4;

        [FieldOffset(0)]
        public SockaddrIn6 Ipv6;

        public static SockaddrInet FromIPAddress(IPAddress address)
        {
            var value = new SockaddrInet();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                value.Ipv4 = new SockaddrIn
                {
                    Family = AF_INET,
                    Port = 0,
                    Address = new InAddr
                    {
                        Value = BitConverter.ToUInt32(bytes, 0)
                    }
                };
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] bytes = address.GetAddressBytes();
                value.Ipv6 = new SockaddrIn6
                {
                    Family = AF_INET6,
                    Port = 0,
                    FlowInfo = 0,
                    Address = new In6Addr
                    {
                        Part1 = BitConverter.ToUInt64(bytes, 0),
                        Part2 = BitConverter.ToUInt64(bytes, 8)
                    },
                    ScopeId = checked((uint)Math.Max(0, address.ScopeId))
                };
            }
            else
            {
                throw new NotSupportedException($"Unsupported address family: {address.AddressFamily}");
            }

            return value;
        }

        public IPAddress? ToIPAddress()
        {
            if (Family == AF_INET)
                return new IPAddress(BitConverter.GetBytes(Ipv4.Address.Value));

            if (Family == AF_INET6)
            {
                byte[] bytes = new byte[16];
                BitConverter.GetBytes(Ipv6.Address.Part1).CopyTo(bytes, 0);
                BitConverter.GetBytes(Ipv6.Address.Part2).CopyTo(bytes, 8);
                return new IPAddress(bytes, Ipv6.ScopeId);
            }

            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrIn
    {
        public ushort Family;
        public ushort Port;
        public InAddr Address;
        public uint Zero1;
        public uint Zero2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InAddr
    {
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrIn6
    {
        public ushort Family;
        public ushort Port;
        public uint FlowInfo;
        public In6Addr Address;
        public uint ScopeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct In6Addr
    {
        public ulong Part1;
        public ulong Part2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpforwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;

        [MarshalAs(UnmanagedType.U1)]
        public bool Loopback;

        [MarshalAs(UnmanagedType.U1)]
        public bool AutoconfigureAddress;

        [MarshalAs(UnmanagedType.U1)]
        public bool Publish;

        [MarshalAs(UnmanagedType.U1)]
        public bool Immortal;

        public uint Age;
        public uint Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpinterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ScopeLevelCount)]
        public uint[] ZoneIndices;

        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }
}
