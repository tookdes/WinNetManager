
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Storage;

/// <summary>运行时状态持久化（冷却/预算/armed 跨重启有效）。</summary>
public sealed class JsonRuntimeStateStore : IRuntimeStateStore
{
    private readonly string _path;

    public JsonRuntimeStateStore(string path) => _path = path;

    public Dictionary<string, RuleRuntimeState> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, RuleRuntimeState>();
            string json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            var states = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, RuleRuntimeState>>(json, RuleJson.SerializerOptions);
            return states ?? new Dictionary<string, RuleRuntimeState>();
        }
        catch { return new Dictionary<string, RuleRuntimeState>(); }
    }

    public void Save(IReadOnlyDictionary<string, RuleRuntimeState> states)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = System.Text.Json.JsonSerializer.Serialize(states, RuleJson.SerializerOptions);
            File.WriteAllText(_path, json, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
