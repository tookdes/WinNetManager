using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinNetManager.Services;

public class NaturalStringComparer : IComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    public int Compare(object? x, object? y)
    {
        return StrCmpLogicalW(x?.ToString() ?? "", y?.ToString() ?? "");
    }

    public static int CompareStrings(string a, string b) => StrCmpLogicalW(a, b);
}

/// <summary>
/// IComparer for ListCollectionView.CustomSort that reads a property by name
/// and compares using StrCmpLogicalW (natural/numeric sort).
/// </summary>
public class NaturalSortByProperty : IComparer
{
    private readonly string? _propertyName;
    private readonly int _direction;

    public NaturalSortByProperty(string? propertyName, ListSortDirection direction)
    {
        _propertyName = propertyName;
        _direction = direction == ListSortDirection.Ascending ? 1 : -1;
    }

    public int Compare(object? x, object? y)
    {
        string sx = GetValue(x);
        string sy = GetValue(y);
        return NaturalStringComparer.CompareStrings(sx, sy) * _direction;
    }

    private string GetValue(object? obj)
    {
        if (obj == null || _propertyName == null) return "";
        var prop = obj.GetType().GetProperty(_propertyName);
        return prop?.GetValue(obj)?.ToString() ?? "";
    }
}
