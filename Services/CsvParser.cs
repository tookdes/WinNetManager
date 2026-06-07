using System.Collections.Generic;
using System.Text;

namespace WinNetManager.Services;

/// <summary>
/// 解析 PowerShell ConvertTo-Csv 输出的单行 CSV。
/// 正确处理双引号转义（"" → "）。
/// </summary>
public static class CsvParser
{
    public static string[] ParseLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result.ToArray();
    }
}
