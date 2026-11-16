using System.Runtime.InteropServices;

namespace SideScreen;

/// <summary>One Windows display output (attached to the desktop or not).</summary>
public sealed record DisplayInfo(
    string DeviceName, string AdapterName, bool IsVirtual, bool Attached, bool Primary,
    int X, int Y, int Width, int Height, int Hz)
{
    public string Label => IsVirtual ? "Extend" : "Mirror";
    public string MirrorDetail => $"Mirror  ·  {DeviceName.Replace(@"\\.\", "")}  {Width}×{Height}";
}

public static unsafe class Displays
{
    // ---------- enumeration ----------

    public static List<DisplayInfo> List()
    {
        var list = new List<DisplayInfo>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            const int Attached = 0x1, Primary = 0x4, MirrorDriver = 0x8;
            if ((dd.StateFlags & MirrorDriver) != 0) continue;
            bool attached = (dd.StateFlags & Attached) != 0;
            bool isVirtual = IsOurVirtual(dd.DeviceString) || IsOurVirtual(dd.DeviceID);
            if (!attached && !isVirtual) continue;

            var dm = NewDevMode();
            int x = 0, y = 0, w = 0, h = 0, hz = 0;
            if (attached && EnumDisplaySettingsEx(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                x = dm.dmPositionX; y = dm.dmPositionY;
                w = dm.dmPelsWidth; h = dm.dmPelsHeight; hz = dm.dmDisplayFrequency;
            }
            list.Add(new DisplayInfo(dd.DeviceName, dd.DeviceString, isVirtual, attached,
                (dd.StateFlags & Primary) != 0, x, y, w, h, hz));
        }
        return list;
    }

    static bool IsOurVirtual(string? s) =>
        s != null && (s.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                   || s.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)
                   || s.Contains("IddSampleDriver", StringComparison.OrdinalIgnoreCase));

    public static DisplayInfo? FindVirtual() => List().FirstOrDefault(d => d.IsVirtual);
    public static DisplayInfo? Find(string deviceName) => List().FirstOrDefault(d => d.DeviceName == deviceName);

    /// <summary>True when the display device offers exactly this mode.</summary>
    public static bool HasMode(string deviceName, int width, int height, int hz)
    {
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettingsEx(deviceName, i, ref dm, 0); i++)
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmDisplayFrequency == hz) return true;
        return false;
    }

    // ---------- attach / detach / mode ----------

    const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const int CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
    const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;

    /// <summary>Attach the display to the desktop (extend) and set its mode. Returns null on success.</summary>
    public static string? AttachAndSetMode(string deviceName, int width, int height, int hz, int position = 0)
    {
        var all = List();
        var cur = all.FirstOrDefault(d => d.DeviceName == deviceName);
        if (cur == null) return "display not found";

        if (!cur.Attached && IsVirtualMonitorCloned())
        {
            // Windows sometimes brings a new monitor up in "Duplicate" mode, where it has no desktop
            // area of its own. Switch the topology to "Extend" (same as Win+P -> Extend).
            SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                all = List();
                cur = all.FirstOrDefault(d => d.DeviceName == deviceName);
                if (cur is { Attached: true }) break;
            }
            if (cur == null) return "display not found";
        }

        // Place it next to the screens that are already there (0 right, 1 left, 2 above, 3 below).
        var others = all.Where(d => d.Attached && d.DeviceName != deviceName).ToList();
        int x = 0, y = 0;
        if (others.Count > 0)
        {
            switch (position)
            {
                case 1: x = others.Min(d => d.X) - width; break;
                case 2: y = others.Min(d => d.Y) - height; break;
                case 3: y = others.Max(d => d.Y + d.Height); break;
                default: x = others.Max(d => d.X + d.Width); break;
            }
        }

        // Try the exact mode first, then gracefully fall back.
        (int w, int h, int f)[] attempts =
        [
            (width, height, hz), (width, height, 60), (width, height, 0),
        ];
        int rc = -1;
        foreach (var (w, h, f) in attempts)
        {
            var dm = NewDevMode();
            dm.dmPositionX = x; dm.dmPositionY = y;
            dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = f;
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | (f > 0 ? DM_DISPLAYFREQUENCY : 0);
            rc = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (rc == 0) break;
        }
        if (rc != 0 && !cur.Attached)
        {
            // Last resort: attach with whatever mode the driver prefers.
            var dm = NewDevMode();
            dm.dmPositionX = x; dm.dmPositionY = y;
            dm.dmFields = DM_POSITION;
            rc = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        }
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return rc == 0 ? null : $"could not set {width}x{height}@{hz} (code {rc})";
    }

    public static void Detach(string deviceName)
    {
        var dm = NewDevMode();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; // all zero = remove from desktop
        ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>True when the virtual monitor is active as a clone of another display (shares its source).</summary>
    static bool IsVirtualMonitorCloned()
    {
        var head = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref head, 0); i++)
        {
            if ((head.StateFlags & 0x1) == 0 || IsOurVirtual(head.DeviceString) || IsOurVirtual(head.DeviceID)) continue;
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint j = 0; EnumDisplayDevices(head.DeviceName, j, ref mon, 0); j++)
                if ((mon.StateFlags & 0x1) != 0 && mon.DeviceID.Contains(@"MONITOR\MTT", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    // ---------- DXGI lookup (adapter index / output index for the capture) ----------

    public readonly record struct DxgiOutput(int Adapter, int Output, int X, int Y, int Width, int Height);

    public static DxgiOutput? FindDxgiOutput(string deviceName)
    {
        var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"); // IDXGIFactory
        if (CreateDXGIFactory1(in iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return null;
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                // IDXGIFactory::EnumAdapters = vtable slot 7 (same order ffmpeg's d3d11va device index uses)
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[7])(factory, a, &adapter);
                if (hr < 0) break;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        // IDXGIAdapter::EnumOutputs = slot 7
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)adapter)[7])(adapter, o, &output);
                        if (hr < 0) break;
                        try
                        {
                            DXGI_OUTPUT_DESC desc;
                            // IDXGIOutput::GetDesc = slot 7
                            hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)(*(void***)output)[7])(output, &desc);
                            if (hr >= 0)
                            {
                                string name = new string(desc.DeviceName);
                                if (string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase))
                                    return new DxgiOutput((int)a, (int)o, desc.Left, desc.Top,
                                        desc.Right - desc.Left, desc.Bottom - desc.Top);
                            }
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return null;
    }

    // ---------- interop ----------

    static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);
}
