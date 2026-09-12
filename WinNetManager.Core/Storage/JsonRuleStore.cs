
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Storage;

/// <summary>规则定义持久化（JSON，version 校验）。损坏时备份原文件并返回空列表，不覆盖丢失用户数据。</summary>
public sealed class JsonRuleStore : IRuleStore
{
    private const int SupportedVersion = 1;
    private readonly string _path;
    /// <summary>上次 Load 是否失败（损坏/版本过高）。失败后 Save 拒绝覆盖原文件，避免把坏文件清成空列表。</summary>
    public bool LastLoadFailed { get; private set; }

    public JsonRuleStore(string path) => _path = path;

    public List<AutomationRule> Load()
    {
        LastLoadFailed = false;
        try
        {
            if (!File.Exists(_path)) return new List<AutomationRule>();
            string json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.GetInt32() > SupportedVersion)
                throw new InvalidOperationException($"规则文件版本 {v.GetInt32()} 不受支持，请升级 WinNetManager。");
            if (doc.RootElement.TryGetProperty("rules", out var rulesEl))
            {
                var rules = System.Text.Json.JsonSerializer.Deserialize<List<AutomationRule>>(rulesEl.GetRawText(), RuleJson.SerializerOptions);
                return rules ?? new List<AutomationRule>();
            }
            return new List<AutomationRule>();
        }
        catch (Exception)
        {
            // 保留损坏文件便于排查，返回空；标记失败，禁止后续 Save 覆盖坏文件
            LastLoadFailed = true;
            try
            {
                string backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_path, backup, overwrite: false);
            }
            catch { }
            return new List<AutomationRule>();
        }
    }

    public void Save(IEnumerable<AutomationRule> rules)
    {
        try
        {
            // 原文件损坏/版本过高时拒绝覆盖，避免静默清空用户规则
            if (LastLoadFailed && File.Exists(_path)) return;
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var payload = new { version = SupportedVersion, savedAt = DateTime.Now, rules = rules.ToList() };
            string json = System.Text.Json.JsonSerializer.Serialize(payload, RuleJson.SerializerOptions);
            // 原子写：先写临时文件再替换，避免崩溃产生截断文件
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* 写失败不致命 */ }
    }
}
