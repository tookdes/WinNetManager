using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class WinNetConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportDate")]
    public DateTime ExportDate { get; set; }

    [JsonPropertyName("persistentRoutes")]
    public List<RouteConfig> PersistentRoutes { get; set; } = new();

    [JsonPropertyName("portProxyRules")]
    public List<ProxyConfig> PortProxyRules { get; set; } = new();
}

public class RouteConfig
{
    [JsonPropertyName("addressFamily")]
    public string AddressFamily { get; set; } = "";

    [JsonPropertyName("destinationPrefix")]
    public string DestinationPrefix { get; set; } = "";

    [JsonPropertyName("nextHop")]
    public string NextHop { get; set; } = "";

    [JsonPropertyName("interfaceAlias")]
    public string InterfaceAlias { get; set; } = "";

    [JsonPropertyName("interfaceIndex")]
    public string InterfaceIndex { get; set; } = "";

    [JsonPropertyName("routeMetric")]
    public string RouteMetric { get; set; } = "";
}

public class ProxyConfig
{
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "";

    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; set; } = "";

    [JsonPropertyName("listenPort")]
    public string ListenPort { get; set; } = "";

    [JsonPropertyName("connectAddress")]
    public string ConnectAddress { get; set; } = "";

    [JsonPropertyName("connectPort")]
    public string ConnectPort { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp";
}

public static class ConfigExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Export(string filePath, WinNetConfig config)
    {
        config.ExportDate = DateTime.Now;
        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    private const int SupportedVersion = 1;

    public static WinNetConfig Import(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var config = JsonSerializer.Deserialize<WinNetConfig>(json, JsonOptions);
        if (config == null) return new WinNetConfig();

        if (config.Version > SupportedVersion)
            throw new InvalidOperationException($"配置文件版本 {config.Version} 不受支持，请升级 WinNetManager。");

        return config;
    }

    // --- 转换辅助 ---

    public static List<RouteConfig> ToRouteConfigs(List<RouteEntry> routes)
    {
        var result = new List<RouteConfig>();
        foreach (var r in routes)
        {
            result.Add(new RouteConfig
            {
                AddressFamily = r.AddressFamily,
                DestinationPrefix = r.DestinationPrefix,
                NextHop = r.NextHop,
                InterfaceAlias = r.InterfaceAlias,
                InterfaceIndex = r.InterfaceIndex,
                RouteMetric = r.RouteMetric,
            });
        }
        return result;
    }

    public static List<RouteEntry> ToRouteEntries(List<RouteConfig> configs)
    {
        var result = new List<RouteEntry>();
        foreach (var c in configs)
        {
            result.Add(new RouteEntry
            {
                AddressFamily = c.AddressFamily,
                DestinationPrefix = c.DestinationPrefix,
                NextHop = c.NextHop,
                InterfaceAlias = c.InterfaceAlias,
                InterfaceIndex = c.InterfaceIndex,
                RouteMetric = c.RouteMetric,
                Store = "PersistentStore",
            });
        }
        return result;
    }

    public static List<ProxyConfig> ToProxyConfigs(List<PortProxyRule> rules)
    {
        var result = new List<ProxyConfig>();
        foreach (var r in rules)
        {
            result.Add(new ProxyConfig
            {
                Direction = r.Direction,
                ListenAddress = r.ListenAddress,
                ListenPort = r.ListenPort,
                ConnectAddress = r.ConnectAddress,
                ConnectPort = r.ConnectPort,
                Protocol = r.Protocol,
            });
        }
        return result;
    }

    public static List<PortProxyRule> ToProxyRules(List<ProxyConfig> configs)
    {
        var result = new List<PortProxyRule>();
        foreach (var c in configs)
        {
            result.Add(new PortProxyRule
            {
                Direction = c.Direction,
                ListenAddress = c.ListenAddress,
                ListenPort = c.ListenPort,
                ConnectAddress = c.ConnectAddress,
                ConnectPort = c.ConnectPort,
                Protocol = c.Protocol,
            });
        }
        return result;
    }
}
