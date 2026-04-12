using System.Runtime.InteropServices;

namespace WinNetManager.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct SP_REMOVEDEVICE_PARAMS
{
    public SP_CLASSINSTALL_HEADER ClassInstallHeader;
    public uint Scope;
    public uint HwProfile;
}

[StructLayout(LayoutKind.Sequential)]
public struct SP_CLASSINSTALL_HEADER
{
    public uint cbSize;
    public uint InstallFunction;
}

public static class NativeMethods
{
    public static readonly Guid GUID_DEVCLASS_NET = new("4D36E972-E325-11CE-BFC1-08002BE10318");

    public const uint DIGCF_DEFAULT = 0x00000001;
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const uint DIGCF_PROFILE = 0x00000008;

    public const uint SPDRP_DEVICEDESC = 0x00000000;
    public const uint SPDRP_HARDWAREID = 0x00000001;
    public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    public const uint SPDRP_CLASSGUID = 0x00000008;

    public const uint DIF_REMOVE = 0x00000005;
    public const uint DI_REMOVEDEVICE_GLOBAL = 0x00000001;

    public const uint CR_SUCCESS = 0x00000000;
    public const uint CR_NO_SUCH_DEVINST = 0x0000000D;

    public const uint DN_DISABLEABLE = 0x00002000;

    public const int ERROR_NO_MORE_ITEMS = 259;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid,
        IntPtr Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        out uint PropertyRegDataType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        char[]? DeviceInstanceId,
        uint DeviceInstanceIdSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_REMOVEDEVICE_PARAMS ClassInstallParams,
        uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    public static extern uint CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    public static string? GetDeviceRegistryPropertyString(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
    {
        SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, null, 0, out uint requiredSize);
        if (requiredSize == 0) return null;

        byte[] buffer = new byte[requiredSize];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, requiredSize, out _))
            return null;

        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static string? GetDeviceInstanceIdString(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
    {
        SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, null, 0, out uint requiredSize);
        if (requiredSize == 0) return null;

        char[] buffer = new char[requiredSize];
        if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, buffer, requiredSize, out _))
            return null;

        return new string(buffer).TrimEnd('\0');
    }
}
