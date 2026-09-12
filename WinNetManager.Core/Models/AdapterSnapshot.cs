
namespace WinNetManager.Core.Models;

/// <summary>
/// 单个网卡的观测快照。Id 用 NetworkInterface.Id（GUID）作为稳定键，
/// Name/Description 仅作展示。GlobalIpv4/GlobalIpv6 为当前选择的稳定全局源地址。
/// </summary>
public sealed class AdapterSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Up { get; set; }
    public string? GlobalIpv4 { get; set; }
    public string? GlobalIpv6 { get; set; }
    public string? Ipv4Gateway { get; set; }
    public string? Ipv6Gateway { get; set; }

    public string? GetGlobalAddress(AddressFamilyKind family)
        => family == AddressFamilyKind.IPv4 ? GlobalIpv4 : GlobalIpv6;

    public string? GetGateway(AddressFamilyKind family)
        => family == AddressFamilyKind.IPv4 ? Ipv4Gateway : Ipv6Gateway;
}
