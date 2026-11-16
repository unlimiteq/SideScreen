using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SideScreen;

public sealed class StreamOptions
{
    public string? MirrorDevice;   // null = extend onto the virtual display
    public int Fps = 120;
    public int BitrateMbps = 80;
    public int Width, Height;      // 0 = the tablet's native resolution (Extend only)
    public int Position;           // where the tablet sits: 0 right, 1 left, 2 above, 3 below
    public bool Touch = true;
}

/// <summary>
/// Owns the whole pipeline: USB tunnel (adb reverse) -> tablet handshake -> virtual display mode ->
/// GPU capture + NVENC (sscapture.exe, zero-copy on the GPU) -> framed elementary stream to the tablet.
/// </summary>
public sealed class StreamSession
{
    public const int Port = 38917;

    // wire protocol: [type:1][length:4 BE][payload]
    const byte MsgHello = 0x01, MsgTouch = 0x02;
    const byte MsgPing = 0x14; // once a second, so the tablet can detect a dead connection
    readonly object _netLock = new();
    const byte MsgFrame = 0x11; // helper packets (config, frame, cursor) are relayed verbatim

    public event Action<string>? Status;
    public event Action<double, double, double>? Stats; // fps, Mbit/s, encode ms
    public event Action? Stopped;

    readonly StreamOptions _opt;
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;
    Process? _capture;
    TcpClient? _client;
    string? _serial;
    string? _attachedVirtual;
    string _lastCaptureError = "";

    public StreamSession(StreamOptions opt)
    {
        _opt = opt;
        Status += msg => Log.Write(msg);
        long lastStat = 0;
        Stats += (fps, mbit, enc) =>
        {
            if (Environment.TickCount64 - lastStat < 5000) return;
            lastStat = Environment.TickCount64;
            Log.Write(FormattableString.Invariant($"stats fps={fps:0.0} mbps={mbit:0.0} enc_ms={enc:0.0}"));
        };
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "stream" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        KillCapture();
    }

    bool Stopping => _cts.IsCancellationRequested;

    void Run()
    {
        TcpListener? listener = null;
        try
        {
            // 1. tablet over USB
            string? lastInfo = null;
            while (!Stopping)
            {
                var (serial, info) = Adb.FindDevice();
                if (serial != null) { _serial = serial; Status?.Invoke($"Tablet {info} connected"); break; }
                if (info != lastInfo) { Status?.Invoke(info); lastInfo = info; }
                _cts.Token.WaitHandle.WaitOne(1500);
            }
            if (Stopping) return;

            Status?.Invoke("Preparing tablet app…");
            var err = Adb.EnsureApp(_serial!);
            if (err != null) { Status?.Invoke(err); return; }

            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start(1);
            Adb.Reverse(_serial!, Port);
            Adb.LaunchApp(_serial!);

            // 2. serve the tablet, forever, until stopped
            long lastTunnel = Environment.TickCount64;
            while (!Stopping)
            {
                if (!listener.Pending())
                {
                    Status?.Invoke("Waiting for tablet app…");
                    if (Environment.TickCount64 - lastTunnel > 3000)
                    {
                        // survives cable re-plugs: the reverse tunnel has to be re-created
                        var (serial, _) = Adb.FindDevice();
                        if (serial != null) { _serial = serial; Adb.Reverse(serial, Port); }
                        lastTunnel = Environment.TickCount64;
                    }
                    _cts.Token.WaitHandle.WaitOne(200);
                    continue;
                }
                using var client = listener.AcceptTcpClient();
                _client = client;
                try { Serve(client); }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
                finally { KillCapture(); _client = null; Stats?.Invoke(0, 0, 0); }
            }
        }
        catch (Exception e)
        {
            Status?.Invoke("Error: " + e.Message);
        }
        finally
        {
            try { listener?.Stop(); } catch { }
            KillCapture();
            if (_attachedVirtual != null) { try { Displays.Detach(_attachedVirtual); } catch { } }
            if (_serial != null) Adb.RemoveReverse(_serial, Port);
            Stopped?.Invoke();
        }
    }

    void Serve(TcpClient client)
    {
        client.NoDelay = true;
        client.SendBufferSize = 256 * 1024; // small: back-pressure drops frames at capture instead of adding latency
        var net = client.GetStream();

        // --- handshake: tablet tells us its panel ---
        var head = new byte[5];
        net.ReadExactly(head);
        int len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (head[0] != MsgHello || len < 12 || len > 64) return;
        var hello = new byte[len];
        net.ReadExactly(hello);
        int tabW = BinaryPrimitives.ReadInt32BigEndian(hello);
        int tabH = BinaryPrimitives.ReadInt32BigEndian(hello.AsSpan(4));
        int tabHz = BinaryPrimitives.ReadInt32BigEndian(hello.AsSpan(8));
        int fps = Math.Clamp(Math.Min(_opt.Fps, tabHz <= 0 ? 60 : tabHz), 30, 240);

        // --- pick / prepare the display to capture ---
        string device;
        if (_opt.MirrorDevice == null)
        {
            var vd = Displays.FindVirtual();
            if (vd == null) { Status?.Invoke("Virtual display driver not installed"); Thread.Sleep(1500); return; }
            int w = _opt.Width > 0 ? _opt.Width : tabW, h = _opt.Height > 0 ? _opt.Height : tabH;
            // custom sizes / rates are added to the driver on the fly (it then reappears as a new device)
            var modeIssue = DriverModes.Ensure(w, h, fps, Status);
            if (modeIssue != null) Status?.Invoke("Display: " + modeIssue);
            vd = Displays.FindVirtual() ?? vd;
            Status?.Invoke($"Creating {w}×{h} @ {fps} Hz display…");
            var modeErr = Displays.AttachAndSetMode(vd.DeviceName, w, h, fps, _opt.Position);
            _attachedVirtual = vd.DeviceName;
            if (modeErr != null) Status?.Invoke("Display: " + modeErr);
            device = vd.DeviceName;
        }
        else device = _opt.MirrorDevice;

        using var touch = new TouchInjector();
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            try { lock (_netLock) net.Write([MsgPing, 0, 0, 0, 0]); }
            catch { try { client.Close(); } catch { } }
        }, null, 1000, 1000);
        var reader = new Thread(() => ReadFromTablet(net, touch)) { IsBackground = true, Name = "tablet-in" };
        reader.Start();

        // --- stream; the capture helper is restarted whenever the desktop changes under it (mode switch, UAC, …) ---
        int quickFailures = 0;
        while (!Stopping && client.Connected)
        {
            Displays.DxgiOutput? dx = null;
            for (int i = 0; i < 25 && dx == null && !Stopping; i++)
            {
                dx = Displays.FindDxgiOutput(device);
                if (dx == null) Thread.Sleep(200);
            }
            if (dx == null) { Status?.Invoke("Display is not available for capture"); Thread.Sleep(1000); continue; }
            var o = dx.Value;
            touch.X = o.X; touch.Y = o.Y; touch.Width = o.Width; touch.Height = o.Height;

            var sw = Stopwatch.StartNew();
            bool sentFrames = Pump(net, o, fps);
            if (Stopping || !client.Connected) break;
            if (!sentFrames && sw.ElapsedMilliseconds < 3000)
            {
                if (++quickFailures >= 3)
                    Status?.Invoke(_lastCaptureError.Contains("0x80070005")
                        ? "Paused – Windows is showing a protected screen (lock / admin prompt)"
                        : "Capture failed: " + (_lastCaptureError.Length > 0 ? _lastCaptureError : "capture helper exited"));
                Thread.Sleep(700);
            }
            else quickFailures = 0;
        }
        touch.ReleaseAll();
    }

    /// <summary>Run one capture/encode helper process and relay its packets. Returns true if frames flowed.</summary>
    bool Pump(NetworkStream net, Displays.DxgiOutput o, int fps)
    {
        // sscapture.exe: Desktop Duplication -> NVENC HEVC, entirely on the GPU, already framed in our wire format
        string args = $"--adapter {o.Adapter} --output {o.Output} --fps {fps} --mbps {_opt.BitrateMbps}";
        var psi = new ProcessStartInfo(Tools.CapturePath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, // the helper exits when this closes, so it can never be orphaned
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception e) { _lastCaptureError = "sscapture.exe not found (" + e.Message + ")"; return false; }
        _capture = p;
        _lastCaptureError = "";
        p.ErrorDataReceived += (_, e) =>
        {
            var line = e.Data?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            if (!line.StartsWith("stat ")) { _lastCaptureError = line; Log.Write("capture: " + line); return; }
            double fpsNow = 0, mbit = 0, encMs = 0;
            foreach (var kv in line[5..].Split(' '))
            {
                var parts = kv.Split('=');
                if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                if (parts[0] == "fps") fpsNow = v; else if (parts[0] == "mbps") mbit = v; else if (parts[0] == "enc_ms") encMs = v;
            }
            Stats?.Invoke(fpsNow, mbit, encMs);
        };
        p.BeginErrorReadLine();

        bool any = false;
        try
        {
            var src = p.StandardOutput.BaseStream;
            byte[] buf = new byte[1 << 20];
            while (!Stopping)
            {
                src.ReadExactly(buf, 0, 5);
                int len = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(1));
                if (len < 0 || len > (64 << 20)) break;
                if (buf.Length < len + 5) Array.Resize(ref buf, len + len / 2 + 5);
                src.ReadExactly(buf, 5, len);
                lock (_netLock) net.Write(buf, 0, len + 5);
                if (!any && buf[0] == MsgFrame)
                {
                    any = true;
                    Status?.Invoke($"Streaming {o.Width}×{o.Height} @ {fps} Hz over USB");
                }
            }
        }
        catch (EndOfStreamException) { /* helper ended (desktop switch / mode change) – caller restarts it */ }
        finally { KillCapture(); }
        return any;
    }

    void ReadFromTablet(NetworkStream net, TouchInjector touch)
    {
        var head = new byte[5];
        var buf = new byte[256];
        var pts = new (int id, int state, int nx, int ny)[10];
        try
        {
            while (!Stopping)
            {
                net.ReadExactly(head);
                int len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
                if (len < 0 || len > buf.Length) break;
                net.ReadExactly(buf, 0, len);
                if (head[0] != MsgTouch || len < 1) continue;
                int count = Math.Min(buf[0], (byte)10);
                if (len < 1 + count * 6) continue;
                for (int i = 0; i < count; i++)
                {
                    int p = 1 + i * 6;
                    pts[i] = (buf[p], buf[p + 1], (buf[p + 2] << 8) | buf[p + 3], (buf[p + 4] << 8) | buf[p + 5]);
                }
                if (_opt.Touch) touch.Inject(pts.AsSpan(0, count));
            }
        }
        catch { }
        // tablet went away: unblock the pump
        try { _client?.Close(); } catch { }
        KillCapture();
    }

    void KillCapture()
    {
        var p = Interlocked.Exchange(ref _capture, null);
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }
}
