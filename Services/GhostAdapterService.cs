using System.Runtime.InteropServices;
using WinNetManager.Interop;
using WinNetManager.Models;

namespace WinNetManager.Services;

public static class GhostAdapterService
{
    public static List<GhostAdapter> GetAllNetworkAdapters()
    {
        var adapters = new List<GhostAdapter>();
        var classGuid = NativeMethods.GUID_DEVCLASS_NET;

        // No DIGCF_PRESENT flag = include ghost (non-present) devices
        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref classGuid, IntPtr.Zero, IntPtr.Zero, 0);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            return adapters;

        try
        {
            uint index = 0;
            var devInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            while (NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfoData))
            {
                string? friendlyName = NativeMethods.GetDeviceRegistryPropertyString(
                    deviceInfoSet, ref devInfoData, NativeMethods.SPDRP_FRIENDLYNAME);
                string? description = NativeMethods.GetDeviceRegistryPropertyString(
                    deviceInfoSet, ref devInfoData, NativeMethods.SPDRP_DEVICEDESC);
                string? hardwareId = NativeMethods.GetDeviceRegistryPropertyString(
                    deviceInfoSet, ref devInfoData, NativeMethods.SPDRP_HARDWAREID);
                string? instanceId = NativeMethods.GetDeviceInstanceIdString(
                    deviceInfoSet, ref devInfoData);

                bool isPresent = IsDevicePresent(devInfoData.DevInst);

                adapters.Add(new GhostAdapter
                {
                    DeviceInstanceId = instanceId ?? "",
                    FriendlyName = friendlyName ?? description ?? "",
                    Description = description ?? "",
                    HardwareId = hardwareId ?? "",
                    IsPresent = isPresent
                });

                index++;
                devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return adapters;
    }

    public static bool RemoveDevice(string deviceInstanceId)
    {
        var classGuid = NativeMethods.GUID_DEVCLASS_NET;
        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref classGuid, IntPtr.Zero, IntPtr.Zero, 0);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            return false;

        try
        {
            uint index = 0;
            var devInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            while (NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfoData))
            {
                string? instId = NativeMethods.GetDeviceInstanceIdString(deviceInfoSet, ref devInfoData);
                if (string.Equals(instId, deviceInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    var removeParams = new SP_REMOVEDEVICE_PARAMS
                    {
                        ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                        {
                            cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                            InstallFunction = NativeMethods.DIF_REMOVE
                        },
                        Scope = NativeMethods.DI_REMOVEDEVICE_GLOBAL,
                        HwProfile = 0
                    };

                    if (!NativeMethods.SetupDiSetClassInstallParams(deviceInfoSet, ref devInfoData,
                        ref removeParams, (uint)Marshal.SizeOf<SP_REMOVEDEVICE_PARAMS>()))
                        return false;

                    return NativeMethods.SetupDiCallClassInstaller(
                        NativeMethods.DIF_REMOVE, deviceInfoSet, ref devInfoData);
                }

                index++;
                devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return false;
    }

    private static bool IsDevicePresent(uint devInst)
    {
        uint result = NativeMethods.CM_Get_DevNode_Status(out _, out _, devInst, 0);
        return result == NativeMethods.CR_SUCCESS;
    }
}
