
using System.Text.RegularExpressions;

namespace WinNetManager.Core;

/// <summary>敏感信息脱敏：URL 查询参数、认证头、代理密码等。审计与导出前调用。</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)(token|key|secret|password|passwd|authorization|access_token|apikey|api_key|sig)=([^&\s#]+)")]
    private static partial Regex SensitiveQueryRegex();

    [GeneratedRegex(@"(?i)(socks5h?://|https?://|socks5://)[^@/\s]+@")]
    private static partial Regex ProxyCredentialsRegex();

    [GeneratedRegex(@"(?i)(authorization|proxy-authorization)\s*[:=]\s*(?:bearer\s+)?[^\s,]+")]
    private static partial Regex HeaderCredentialsRegex();

    /// <summary>把字符串中可能含的凭据替换为 ***。</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        var s = SensitiveQueryRegex().Replace(input, "$1=***");
        // 代理/URL 里的 user:pass@ 一律替换为 ***@（含 IPv6 方括号写法）
        s = ProxyCredentialsRegex().Replace(s, m => m.Groups[1].Value + "***@");
        s = HeaderCredentialsRegex().Replace(s, "$1: ***");
        return s;
    }

    /// <summary>单个命令行参数脱敏（用于命令预览与审计）。</summary>
    public static string RedactArg(string arg) => Redact(arg);

}
