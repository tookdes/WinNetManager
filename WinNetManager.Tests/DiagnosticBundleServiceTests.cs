using WinNetManager.Services;
using Xunit;

namespace WinNetManager.Tests;

public class DiagnosticBundleServiceTests
{
    [Theory]
    [InlineData(0, "", "", 0, "EMPTY")]
    [InlineData(0, "", "some output", 0, "OK")]
    [InlineData(0, "", "partial output", 1, "WARN")]
    [InlineData(1, "", "", 0, "FAIL")]
    [InlineData(0, "stderr", "partial output", 0, "WARN")]
    public void ClassifyStep_DistinguishesEmptyWarningAndFailure(
        int exitCode,
        string error,
        string output,
        int psErrors,
        string expected)
    {
        Assert.Equal(expected,
            DiagnosticBundleService.ClassifyStep(exitCode, error, output, psErrors));
    }

    [Theory]
    [InlineData("2400:3200::1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    public void IsGlobalUnicastIpv6_ExcludesUlaAndLinkLocal(string address, bool expected)
    {
        Assert.Equal(expected,
            DiagnosticBundleService.IsGlobalUnicastIpv6(System.Net.IPAddress.Parse(address)));
    }

    [Fact]
    public void PingProbe_NoPacketsSent_HasUnknownLoss()
    {
        var probe = new PingProbeResult
        {
            Target = "unresolved.example",
            Kind = "Domain / Test",
            Sent = 0,
            Received = 0,
            DnsStatus = "Timeout"
        };

        Assert.True(double.IsNaN(probe.LossPercent));
    }

    [Fact]
    public void AnalyzeConnectivity_DoesNotClaimGatewayReachable_WhenNoGatewayExists()
    {
        var findings = new List<string>();
        var pings = new List<PingProbeResult>
        {
            Ping("223.5.5.5", "Public IPv4 / AliDNS", received: 0),
            Ping("119.29.29.29", "Public IPv4 / DNSPod", received: 0)
        };
        int warnings = 0;

        DiagnosticBundleService.AnalyzeConnectivity(findings, pings, new List<HttpProbeResult>(), ref warnings);

        Assert.DoesNotContain(findings, f => f.Contains("网关可达", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeConnectivity_DowngradesPublicIcmpFailure_WhenDirectHttpResponds()
    {
        var findings = new List<string>();
        var pings = new List<PingProbeResult>
        {
            Ping("192.168.1.1", "Gateway", received: 5),
            Ping("223.5.5.5", "Public IPv4 / AliDNS", received: 0),
            Ping("119.29.29.29", "Public IPv4 / DNSPod", received: 0)
        };
        var http = new List<HttpProbeResult>
        {
            new()
            {
                Url = "https://www.baidu.com/",
                Mode = "Direct (proxy disabled)",
                ResponseReceived = true,
                Success = true,
                StatusCode = 200
            }
        };
        int warnings = 0;

        DiagnosticBundleService.AnalyzeConnectivity(findings, pings, http, ref warnings);

        Assert.Contains(findings, f => f.Contains("公网 ICMP", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Contains("默认路由之后", StringComparison.Ordinal));
        Assert.Equal(0, warnings);
    }

    [Fact]
    public void AnalyzeConnectivity_DnsTimeout_IsNotLabeledAsInterception()
    {
        var findings = new List<string>();
        var pings = new List<PingProbeResult>
        {
            Ping("223.5.5.5", "Public IPv4 / AliDNS", received: 5),
            Domain("www.baidu.com", "Timeout"),
            Domain("www.qq.com", "Timeout"),
            Domain("www.microsoft.com", "Timeout")
        };
        int warnings = 0;

        DiagnosticBundleService.AnalyzeConnectivity(findings, pings, new List<HttpProbeResult>(), ref warnings);

        Assert.Contains(findings, f => f.Contains("解析均超时", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Contains("不能仅凭超时认定为代理接管", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.StartsWith("[危险]", StringComparison.Ordinal) &&
                                              f.Contains("DNS", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeConnectivity_DirectWorksButProxyFails_FlagsProxyPath()
    {
        var findings = new List<string>();
        var http = new List<HttpProbeResult>
        {
            new()
            {
                Url = "https://www.baidu.com/",
                Mode = "Direct (proxy disabled)",
                ResponseReceived = true,
                Success = true,
                StatusCode = 200
            },
            new()
            {
                Url = "https://www.baidu.com/",
                Mode = "System proxy/default",
                ResponseReceived = false,
                Success = false,
                Detail = "connection refused"
            }
        };
        int warnings = 0;

        DiagnosticBundleService.AnalyzeConnectivity(findings, new List<PingProbeResult>(), http, ref warnings);

        Assert.Contains(findings, f => f.Contains("系统代理路径全部失败", StringComparison.Ordinal));
        Assert.Equal(1, warnings);
    }

    private static PingProbeResult Ping(string target, string kind, int received) =>
        new()
        {
            Target = target,
            Kind = kind,
            Sent = 5,
            Received = received,
            ResolvedAddress = target
        };

    private static PingProbeResult Domain(string target, string dnsStatus) =>
        new()
        {
            Target = target,
            Kind = "Domain / Test",
            Sent = 5,
            Received = 0,
            DnsStatus = dnsStatus,
            ResolvedAddress = "",
            Error = dnsStatus == "Timeout" ? "DNS resolution timed out" : "DNS error"
        };
}
