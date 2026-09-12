
using System.Text;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Storage;

/// <summary>追加式 JSONL 审计日志，按天分文件。写入前统一脱敏。</summary>
public sealed class JsonlAuditLog : IAuditLog
{
    private readonly string _dir;
    private static readonly System.Text.Json.JsonSerializerOptions CompactJsonOptions = new(RuleJson.SerializerOptions)
    {
        WriteIndented = false,
    };

    public JsonlAuditLog(string dir) => _dir = dir;

    public void Write(AuditEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string file = Path.Combine(_dir, $"audit-{DateTime.Now:yyyy-MM-dd}.jsonl");
            var safe = new AuditEntry
            {
                Timestamp = entry.Timestamp,
                CorrelationId = entry.CorrelationId,
                RuleId = entry.RuleId,
                RuleName = entry.RuleName,
                Kind = entry.Kind,
                Detail = Redactor.Redact(entry.Detail),
                ConditionSummary = Redactor.Redact(entry.ConditionSummary),
                Evidence = Redactor.Redact(entry.Evidence),
                ActionType = entry.ActionType,
                ActionParams = Redactor.Redact(entry.ActionParams),
                CommandPreview = Redactor.Redact(entry.CommandPreview),
                Ok = entry.Ok,
                ExitCode = entry.ExitCode,
                Output = Redactor.Redact(entry.Output),
            };
            // JSONL 必须一条记录一行；RuleJson 的全局选项为了规则文件可读而启用了缩进，
            // 这里必须显式关闭，否则历史读取无法按记录恢复。
            string json = System.Text.Json.JsonSerializer.Serialize(safe, CompactJsonOptions);
            File.AppendAllText(file, json + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* 审计写失败不致命 */ }
    }

    /// <summary>
    /// 读取最近的审计记录，供 UI 在重新启动后恢复历史视图。
    /// 单行损坏（例如异常退出留下半行）会被跳过，不影响其他记录。
    /// </summary>
    public IReadOnlyList<AuditEntry> ReadRecent(int maxCount = 200)
    {
        if (maxCount <= 0) return Array.Empty<AuditEntry>();
        var result = new List<AuditEntry>(Math.Min(maxCount, 200));
        try
        {
            if (!Directory.Exists(_dir)) return result;
            var files = Directory.GetFiles(_dir, "audit-*.jsonl")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string content;
                try { content = File.ReadAllText(file, Encoding.UTF8); }
                catch { continue; }

                // 同时兼容旧版本错误写成“带缩进的多行 JSON 串”的审计文件。
                // 扫描顶层对象边界，而不是简单按行解析。
                var objects = ExtractJsonObjects(content);
                for (int i = objects.Count - 1; i >= 0 && result.Count < maxCount; i--)
                {
                    try
                    {
                        var item = System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(objects[i], RuleJson.SerializerOptions);
                        if (item != null) result.Add(item);
                    }
                    catch { /* 忽略单行损坏 */ }
                }
                if (result.Count >= maxCount) break;
            }
        }
        catch { }
        result.Reverse();
        return result;
    }

    private static List<string> ExtractJsonObjects(string content)
    {
        var objects = new List<string>();
        int depth = 0;
        int start = -1;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    objects.Add(content[start..(i + 1)]);
                    start = -1;
                }
            }
        }
        return objects;
    }
}
