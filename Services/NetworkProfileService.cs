using Microsoft.Win32;
using WinNetManager.Models;

namespace WinNetManager.Services;

public static class NetworkProfileService
{
    private const string ProfilesKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
    private const string SignaturesUnmanagedPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Unmanaged";
    private const string SignaturesManagedPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Managed";

    public static List<NetworkProfile> GetAllProfiles()
    {
        var profiles = new List<NetworkProfile>();
        using var profilesKey = Registry.LocalMachine.OpenSubKey(ProfilesKeyPath);
        if (profilesKey == null) return profiles;

        foreach (string guidStr in profilesKey.GetSubKeyNames())
        {
            if (!Guid.TryParse(guidStr, out Guid guid)) continue;
            using var subKey = profilesKey.OpenSubKey(guidStr);
            if (subKey == null) continue;

            var profile = new NetworkProfile
            {
                Guid = guid,
                ProfileName = subKey.GetValue("ProfileName") as string ?? "",
                Description = subKey.GetValue("Description") as string ?? "",
                Category = (NetworkCategory)(int)(subKey.GetValue("Category") ?? 0),
                NameType = (int)(subKey.GetValue("NameType") ?? 0),
                Managed = ((int)(subKey.GetValue("Managed") ?? 0)) != 0,
                DateCreated = ParseFileTimeBlob(subKey.GetValue("DateCreated") as byte[]),
                DateLastConnected = ParseFileTimeBlob(subKey.GetValue("DateLastConnected") as byte[]),
            };
            profiles.Add(profile);
        }

        return profiles;
    }

    public static void RenameProfile(Guid guid, string newName)
    {
        string keyPath = $@"{ProfilesKeyPath}\{guid:B}";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.SetValue("ProfileName", newName);
    }

    public static void SetCategory(Guid guid, NetworkCategory category)
    {
        string keyPath = $@"{ProfilesKeyPath}\{guid:B}";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.SetValue("Category", (int)category, RegistryValueKind.DWord);
    }

    public static void DeleteProfile(Guid guid)
    {
        string guidStr = guid.ToString("B");

        using var profilesKey = Registry.LocalMachine.OpenSubKey(ProfilesKeyPath, writable: true);
        profilesKey?.DeleteSubKeyTree(guidStr, throwOnMissingSubKey: false);

        DeleteMatchingSignature(SignaturesUnmanagedPath, guid);
        DeleteMatchingSignature(SignaturesManagedPath, guid);
    }

    private static void DeleteMatchingSignature(string signaturesPath, Guid profileGuid)
    {
        using var sigKey = Registry.LocalMachine.OpenSubKey(signaturesPath, writable: true);
        if (sigKey == null) return;

        foreach (string subName in sigKey.GetSubKeyNames())
        {
            using var sub = sigKey.OpenSubKey(subName);
            if (sub == null) continue;

            string? profileGuidValue = sub.GetValue("ProfileGuid") as string;
            if (profileGuidValue != null &&
                Guid.TryParse(profileGuidValue, out Guid parsed) &&
                parsed == profileGuid)
            {
                sigKey.DeleteSubKeyTree(subName, throwOnMissingSubKey: false);
                break;
            }
        }
    }

    /// <summary>
    /// Parses a SYSTEMTIME-style 16-byte blob (used by NetworkList Profiles)
    /// into a DateTime. Returns null if the data is invalid.
    /// Layout: wYear(0), wMonth(2), wDayOfWeek(4) — skipped, wDay(6), wHour(8), wMinute(10), wSecond(12), wMilliseconds(14).
    /// Defends against FILETIME (8 bytes) by checking length, and against garbage by range checks.
    /// </summary>
    private static DateTime? ParseFileTimeBlob(byte[]? data)
    {
        if (data == null || data.Length < 16) return null;
        try
        {
            int year = BitConverter.ToInt16(data, 0);
            int month = BitConverter.ToInt16(data, 2);
            // offset 4 = wDayOfWeek, not needed for DateTime
            int day = BitConverter.ToInt16(data, 6);
            int hour = BitConverter.ToInt16(data, 8);
            int minute = BitConverter.ToInt16(data, 10);
            int second = BitConverter.ToInt16(data, 12);
            int ms = BitConverter.ToInt16(data, 14);
            if (year < 1601 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31)
                return null;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 || ms < 0 || ms > 999)
                return null;
            return new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Utc).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }
}
