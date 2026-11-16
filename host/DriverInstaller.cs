using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SideScreen;

/// <summary>
/// Installs the open-source Virtual Display Driver (MttVDD, an IddCx indirect display driver) as a
/// root-enumerated device – the same thing "devcon install MttVDD.inf Root\MttVDD" does. Needs admin.
/// </summary>
public static class DriverInstaller
{
    const string HardwareId = @"Root\MttVDD";
    const string SettingsDir = @"C:\VirtualDisplayDriver";

    public static string? DriverFolder => Tools.FindFile(Path.Combine("driver", "MttVDD.inf")) is { } inf
        ? Path.GetDirectoryName(inf) : null;

    public static bool IsInstalled()
    {
        var display = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiGetClassDevs(ref display, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new byte[1024];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (SetupDiGetDeviceRegistryProperty(set, ref data, 1 /* SPDRP_HARDWAREID */, out _, buf, buf.Length, out int needed))
                {
                    var ids = Encoding.Unicode.GetString(buf, 0, Math.Min(needed, buf.Length));
                    if (ids.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return false;
    }

    /// <summary>Runs elevated. Returns null on success, otherwise an error message.</summary>
    public static string? Install()
    {
        var folder = DriverFolder;
        if (folder == null) return "Driver files (driver\\MttVDD.inf) were not found next to SideScreen.exe.";
        var inf = Path.Combine(folder, "MttVDD.inf");

        try
        {
            // The driver reads its mode list from here; ours advertises the tablet's native 2960x1848 @ 120 Hz.
            Directory.CreateDirectory(SettingsDir);
            File.Copy(Path.Combine(folder, "vdd_settings.xml"), Path.Combine(SettingsDir, "vdd_settings.xml"), true);

            if (!IsInstalled())
            {
                var className = new StringBuilder(64);
                if (!SetupDiGetINFClass(inf, out Guid classGuid, className, className.Capacity, out _))
                    throw new Win32Exception();
                IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
                if (set == new IntPtr(-1)) throw new Win32Exception();
                try
                {
                    var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiCreateDeviceInfo(set, className.ToString(), ref classGuid, null, IntPtr.Zero, 1 /* DICD_GENERATE_ID */, ref data))
                        throw new Win32Exception();
                    var hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                    if (!SetupDiSetDeviceRegistryProperty(set, ref data, 1 /* SPDRP_HARDWAREID */, hwid, hwid.Length))
                        throw new Win32Exception();
                    if (!SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, set, ref data))
                        throw new Win32Exception();
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }

            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, 1 /* INSTALLFLAG_FORCE */, out _))
                throw new Win32Exception();
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetINFClass(string infName, out Guid classGuid, StringBuilder className, int classNameSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwndParent, int creationFlags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int regType, byte[] buffer, int bufferSize, out int requiredSize);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath, int installFlags, out bool rebootRequired);
}
