using System.Runtime.InteropServices;

namespace SideScreen;

/// <summary>Turns touch points coming from the tablet into real Windows touch input on the target display.</summary>
public sealed class TouchInjector : IDisposable
{
    const int MaxContacts = 10;
    readonly object _lock = new();
    readonly Dictionary<int, (int x, int y)> _active = new();
    readonly System.Threading.Timer _keepAlive;
    long _lastInject;
    bool _ready;

    public int X, Y, Width, Height; // target display rectangle in desktop pixels

    public TouchInjector()
    {
        _ready = InitializeTouchInjection(MaxContacts, 1 /* TOUCH_FEEDBACK_DEFAULT */);
        // Windows cancels injected contacts that are not refreshed, so re-send held touches.
        _keepAlive = new System.Threading.Timer(_ => KeepAlive(), null, 50, 50);
    }

    /// <param name="points">id, state (0 down, 1 move, 2 up), x and y normalised to 0..65535</param>
    public void Inject(ReadOnlySpan<(int id, int state, int nx, int ny)> points)
    {
        if (!_ready || Width <= 0 || points.Length == 0) return;
        lock (_lock)
        {
            var infos = new POINTER_TOUCH_INFO[Math.Min(points.Length, MaxContacts)];
            for (int i = 0; i < infos.Length; i++)
            {
                var p = points[i];
                int px = X + (int)((long)p.nx * (Width - 1) / 65535);
                int py = Y + (int)((long)p.ny * (Height - 1) / 65535);
                bool known = _active.ContainsKey(p.id);
                int flags;
                if (p.state == 2)
                {
                    if (!known) { infos[i] = default; continue; }
                    flags = POINTER_FLAG_UP;
                    _active.Remove(p.id);
                }
                else if (!known || p.state == 0)
                {
                    flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    _active[p.id] = (px, py);
                }
                else
                {
                    flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    _active[p.id] = (px, py);
                }
                infos[i] = Make(p.id, flags, px, py);
            }
            infos = infos.Where(t => t.pointerInfo.pointerType != 0).ToArray();
            if (infos.Length > 0) InjectTouchInput(infos.Length, infos);
            _lastInject = Environment.TickCount64;
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_active.Count == 0) return;
            var infos = _active.Select(kv => Make(kv.Key, POINTER_FLAG_UP, kv.Value.x, kv.Value.y)).ToArray();
            _active.Clear();
            if (_ready) InjectTouchInput(infos.Length, infos);
        }
    }

    void KeepAlive()
    {
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            if (_active.Count == 0 || Environment.TickCount64 - _lastInject < 80) return;
            var infos = _active.Select(kv => Make(kv.Key,
                POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, kv.Value.x, kv.Value.y)).ToArray();
            InjectTouchInput(infos.Length, infos);
            _lastInject = Environment.TickCount64;
        }
        finally { Monitor.Exit(_lock); }
    }

    static POINTER_TOUCH_INFO Make(int id, int flags, int x, int y)
    {
        var t = new POINTER_TOUCH_INFO();
        t.pointerInfo.pointerType = 2; // PT_TOUCH
        t.pointerInfo.pointerId = (uint)id;
        t.pointerInfo.pointerFlags = flags;
        t.pointerInfo.ptPixelLocation = new POINT { x = x, y = y };
        t.touchFlags = 0;
        t.touchMask = 0x1 | 0x4; // TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE
        t.rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 };
        t.pressure = 512;
        return t;
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }

    const int POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4,
              POINTER_FLAG_DOWN = 0x10000, POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public int pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(int count, [In] POINTER_TOUCH_INFO[] contacts);
}
