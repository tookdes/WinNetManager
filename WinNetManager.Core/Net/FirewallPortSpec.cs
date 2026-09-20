namespace WinNetManager.Core.Net;

public static class FirewallPortSpec
{
    public static bool TryParse(string? input, out List<string> ports, out string error)
    {
        ports = new List<string>();
        error = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "端口不能为空。";
            return false;
        }

        var parts = input.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            if (part.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                ports.Add("Any");
                continue;
            }

            int dash = part.IndexOf('-');
            if (dash > 0 && dash < part.Length - 1 && part.IndexOf('-', dash + 1) < 0)
            {
                if (!int.TryParse(part[..dash], out int start)
                    || !int.TryParse(part[(dash + 1)..], out int end)
                    || start < 1 || end > 65535 || start > end)
                {
                    error = "端口范围无效：" + part;
                    return false;
                }
                ports.Add($"{start}-{end}");
                continue;
            }

            if (!int.TryParse(part, out int port) || port < 1 || port > 65535)
            {
                error = "端口无效：" + part;
                return false;
            }
            ports.Add(port.ToString());
        }

        if (ports.Count == 0)
        {
            error = "端口不能为空。";
            return false;
        }

        return true;
    }

    public static string Join(IEnumerable<string> ports) => string.Join(",", ports);

    public static string ToPowerShellArray(IEnumerable<string> ports)
        => string.Join(",", ports.Select(p => "'" + p.Replace("'", "''") + "'"));
}
