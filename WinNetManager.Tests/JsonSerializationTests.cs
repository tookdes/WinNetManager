
using System.Text.Json;
using WinNetManager.Core;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Models;
using WinNetManager.Core.Storage;
using Xunit;

namespace WinNetManager.Tests;

public class JsonSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesPolymorphicConditionAndActions()
    {
        var rule = RuleTemplates.FallbackWhenAllDown(
            "adapter-a", "adapter-b", "adapter-c",
            "1.1.1.1", "8.8.8.8", "https://example.com/hook", "user:pass@127.0.0.1:1080");

        string json = JsonSerializer.Serialize(rule, RuleJson.SerializerOptions);
        var back = JsonSerializer.Deserialize<AutomationRule>(json, RuleJson.SerializerOptions);

        Assert.NotNull(back);
        Assert.IsType<ConditionGroup>(back!.Condition);
        Assert.Equal(2, back.Condition.Children.Count);
        Assert.All(back.Condition.Children, c => Assert.IsType<PingFailCondition>(c));
        Assert.Contains(back.Actions, a => a is HttpRequestAction h && h.SocksProxy != null);
        Assert.Contains(back.Actions, a => a is HttpRequestAction h2 && h2.RunWhen == RunWhenMode.OnlyOnPreviousFailure);
    }

    [Fact]
    public void RuleStore_SaveLoad_RoundTrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"wnm_rules_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonRuleStore(tmp);
            var rules = new List<AutomationRule> { RuleTemplates.Ipv6SelfHealSoft("adapter-a", "2400:3200::1") };
            store.Save(rules);
            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal(rules[0].Name, loaded[0].Name);
            Assert.Equal(rules[0].Condition.Children.Count, loaded[0].Condition.Children.Count);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void Redactor_MasksCredentials()
    {
        Assert.DoesNotContain("secret-token", Redactor.Redact("https://example.com/hook?token=secret-token&x=1"));
        Assert.DoesNotContain("mypass", Redactor.Redact("socks5h://user:mypass@host:1080"));
        Assert.DoesNotContain("mypass", Redactor.Redact("Authorization: Bearer mypass"));
    }

    [Fact]
    public void AuditLog_ReadRecent_RestoresCrossProcessHistory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wnm_audit_{Guid.NewGuid():N}");
        try
        {
            var log = new JsonlAuditLog(dir);
            log.Write(new AuditEntry { Timestamp = new DateTime(2026, 9, 3, 10, 0, 0), Kind = AuditEventKind.EngineStart, Detail = "start" });
            log.Write(new AuditEntry { Timestamp = new DateTime(2026, 9, 3, 10, 1, 0), Kind = AuditEventKind.Evaluate, RuleName = "r", Detail = "matched" });

            var recent = log.ReadRecent(10);

            Assert.Equal(2, recent.Count);
            Assert.Equal(AuditEventKind.EngineStart, recent[0].Kind);
            Assert.Equal("matched", recent[1].Detail);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AuditLog_ReadRecent_ParsesLegacyIndentedObjects()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wnm_audit_legacy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var options = new JsonSerializerOptions(RuleJson.SerializerOptions) { WriteIndented = true };
            string first = JsonSerializer.Serialize(new AuditEntry
            {
                Timestamp = new DateTime(2026, 9, 2, 10, 0, 0),
                Kind = AuditEventKind.Trigger,
                Detail = "first",
            }, options);
            string second = JsonSerializer.Serialize(new AuditEntry
            {
                Timestamp = new DateTime(2026, 9, 2, 10, 1, 0),
                Kind = AuditEventKind.ActionEnd,
                Detail = "second",
            }, options);
            File.WriteAllText(Path.Combine(dir, "audit-2026-09-02.jsonl"), first + Environment.NewLine + second);

            var recent = new JsonlAuditLog(dir).ReadRecent(10);

            Assert.Equal(2, recent.Count);
            Assert.Equal("first", recent[0].Detail);
            Assert.Equal("second", recent[1].Detail);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
