namespace WinNetManager.Core.Net;

/// <summary>
/// Combines the CIDR editor address box with the prefix-length dropdown.
/// The address box often holds a bare IP (e.g. "::" for default) while /0 lives in the combo;
/// completing a bare IPv6 address to /128 would silently destroy a default route.
/// </summary>
public static class CidrPrefix
{
    public static string Resolve(string? addressOrCidr, string? uiPrefixLength, string family)
    {
        string prefix = addressOrCidr?.Trim() ?? "";
        if (string.IsNullOrEmpty(prefix))
            return prefix;

        if (prefix.Contains('/'))
            return prefix;

        if (!string.IsNullOrWhiteSpace(uiPrefixLength)
            && int.TryParse(uiPrefixLength.Trim(), out int length)
            && length >= 0)
        {
            return $"{prefix}/{length}";
        }

        return family == "IPv6" ? $"{prefix}/128" : $"{prefix}/32";
    }
}
