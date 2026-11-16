package com.sidescreen.app;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Display;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.TextView;

import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Receives an HEVC/H.264 elementary stream from the PC over the USB tunnel (adb reverse -> 127.0.0.1)
 * and pushes it straight through the hardware decoder onto a SurfaceView. Touches go back to the PC.
 */
public class MainActivity extends Activity implements SurfaceHolder.Callback {
    static final int PORT = 38917;
    static final int MSG_HELLO = 0x01, MSG_TOUCH = 0x02, MSG_CONFIG = 0x10, MSG_FRAME = 0x11,
            MSG_CURSOR_SHAPE = 0x12, MSG_CURSOR_POS = 0x13, MSG_PING = 0x14;
    static final long DEAD_AFTER_NS = 3_000_000_000L; // no packet (not even a heartbeat) for 3 s = connection lost

    private final Handler ui = new Handler(Looper.getMainLooper());
    private FrameLayout root;
    private SurfaceView surfaceView;
    private TextView status;
    private ImageView cursorView;
    private TextView banner;
    private TextView statsView;
    private volatile boolean statsOn;
    private volatile long lastPacketNs;
    private volatile boolean streaming;      // had a picture on this connection
    private volatile boolean lostConnection; // the last connection ended unexpectedly
    // live numbers for the stats overlay
    private final java.util.concurrent.atomic.AtomicLong bytesIn = new java.util.concurrent.atomic.AtomicLong();
    private final java.util.concurrent.atomic.AtomicInteger framesShown = new java.util.concurrent.atomic.AtomicInteger();
    private final java.util.concurrent.atomic.AtomicLong decodeNsSum = new java.util.concurrent.atomic.AtomicLong();
    private final java.util.concurrent.ConcurrentHashMap<Long, Long> queuedAt = new java.util.concurrent.ConcurrentHashMap<>();
    private long threeFingerDownAt;

    // PC mouse pointer, drawn as an overlay so it moves independently of the video
    private volatile int cursorX, cursorY, cursorW, cursorH, videoW, videoH, videoFps;
    private volatile boolean cursorVisible;
    private final AtomicBoolean cursorUpdatePending = new AtomicBoolean();

    private volatile Surface surface;
    private volatile boolean running;
    private volatile Socket socket;
    private Thread netThread;
    private final LinkedBlockingQueue<byte[]> sendQueue = new LinkedBlockingQueue<>();

    private MediaCodec codec;
    private Thread outputThread;
    private volatile boolean codecAlive;

    private int panelW, panelH, panelHz;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        // Ask for the panel's fastest mode at native resolution (120 Hz on the Tab S8 Ultra).
        Display display = getDisplay();
        Display.Mode current = display.getMode();
        Display.Mode best = current;
        for (Display.Mode m : display.getSupportedModes()) {
            if (m.getPhysicalWidth() == current.getPhysicalWidth()
                    && m.getPhysicalHeight() == current.getPhysicalHeight()
                    && m.getRefreshRate() > best.getRefreshRate()) best = m;
        }
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.preferredDisplayModeId = best.getModeId();
        lp.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
        getWindow().setAttributes(lp);
        panelW = Math.max(best.getPhysicalWidth(), best.getPhysicalHeight());
        panelH = Math.min(best.getPhysicalWidth(), best.getPhysicalHeight());
        panelHz = Math.round(best.getRefreshRate());

        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        surfaceView = new SurfaceView(this);
        surfaceView.getHolder().addCallback(this);
        surfaceView.setOnTouchListener(this::onVideoTouch);
        root.addView(surfaceView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));

        // A SurfaceView marks the window as transparent everywhere except where sibling views were
        // at layout time, so overlays that move (the pointer) would be clipped by the compositor.
        // This full-screen view, which counts as "drawing", keeps the whole window composited.
        root.addView(new View(this), new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        cursorView = new ImageView(this);
        cursorView.setScaleType(ImageView.ScaleType.FIT_XY);
        cursorView.setVisibility(View.GONE);
        root.addView(cursorView, new FrameLayout.LayoutParams(1, 1, Gravity.TOP | Gravity.LEFT));

        // short confirmation shown when the picture starts (an empty desktop can be plain black)
        banner = new TextView(this);
        banner.setTextColor(0xFFF2F4F8);
        banner.setTextSize(16);
        banner.setGravity(Gravity.CENTER);
        banner.setLineSpacing(0, 1.25f);
        banner.setPadding(56, 30, 56, 30);
        android.graphics.drawable.GradientDrawable pill = new android.graphics.drawable.GradientDrawable();
        pill.setColor(0xE61E2129);
        pill.setCornerRadius(36);
        pill.setStroke(2, 0xFF4C8DFF);
        banner.setBackground(pill);
        banner.setVisibility(View.GONE);
        FrameLayout.LayoutParams bp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER_HORIZONTAL | Gravity.BOTTOM);
        bp.bottomMargin = 120;
        root.addView(banner, bp);

        statsView = new TextView(this);
        statsView.setTextColor(0xFFF2F4F8);
        statsView.setTextSize(13);
        statsView.setTypeface(android.graphics.Typeface.MONOSPACE);
        statsView.setPadding(28, 18, 28, 18);
        android.graphics.drawable.GradientDrawable statsBg = new android.graphics.drawable.GradientDrawable();
        statsBg.setColor(0xCC14161B);
        statsBg.setCornerRadius(20);
        statsView.setBackground(statsBg);
        statsView.setVisibility(View.GONE);
        FrameLayout.LayoutParams sp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.RIGHT);
        sp.topMargin = 32; sp.rightMargin = 32;
        root.addView(statsView, sp);

        status = new TextView(this);
        status.setTextColor(0xFF8D94A3);
        status.setTextSize(18);
        status.setGravity(Gravity.CENTER);
        status.setLineSpacing(0, 1.3f);
        status.setBackgroundColor(0xFF14161B);
        root.addView(status, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        showStatus("SideScreen\nWaiting for the PC  ·  press Start in SideScreen on Windows");

        setContentView(root);
        ui.postDelayed(tick, 1000);
        getWindow().setDecorFitsSystemWindows(false);
        hideSystemBars();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemBars();
    }

    private void hideSystemBars() {
        WindowInsetsController c = getWindow().getInsetsController();
        if (c == null) return;
        c.hide(WindowInsets.Type.systemBars());
        c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
    }

    private void showStatus(final String text) {
        ui.post(() -> {
            if (text == null) status.setVisibility(View.GONE);
            else { status.setText(text); status.setVisibility(View.VISIBLE); }
        });
    }

    // ------------------------------------------------------------------ surface lifecycle

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        surface = holder.getSurface();
        try { surface.setFrameRate(panelHz, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE); } catch (Exception ignored) { }
        running = true;
        netThread = new Thread(this::networkLoop, "net");
        netThread.start();
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        running = false;
        closeSocket();
        if (netThread != null) {
            try { netThread.join(1500); } catch (InterruptedException ignored) { }
        }
        releaseCodec();
        surface = null;
    }

    private void closeSocket() {
        Socket s = socket;
        socket = null;
        if (s != null) try { s.close(); } catch (Exception ignored) { }
    }

    // ------------------------------------------------------------------ network

    private void networkLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_DISPLAY);
        byte[] frame = new byte[1 << 20];
        while (running) {
            Thread sender = null;
            try {
                Socket s = new Socket();
                s.setTcpNoDelay(true);
                s.setReceiveBufferSize(1 << 20);
                s.connect(new InetSocketAddress("127.0.0.1", PORT), 1000);
                socket = s;
                DataInputStream in = new DataInputStream(new BufferedInputStream(s.getInputStream(), 1 << 17));
                final OutputStream out = s.getOutputStream();

                ByteBuffer hello = ByteBuffer.allocate(5 + 16);
                hello.put((byte) MSG_HELLO).putInt(16).putInt(panelW).putInt(panelH).putInt(panelHz)
                        .putInt(getResources().getDisplayMetrics().densityDpi);
                out.write(hello.array());
                out.flush();

                sendQueue.clear();
                sender = new Thread(() -> {
                    try {
                        while (true) { out.write(sendQueue.take()); out.flush(); }
                    } catch (Exception ignored) { }
                }, "sender");
                sender.start();
                lastPacketNs = System.nanoTime();
                streaming = false;
                // The USB tunnel accepts a connection even when the PC side is gone or frozen, so the
                // link only counts as live once real data arrives (first packet, below).
                boolean heardFromPc = false;

                while (running) {
                    int type = in.readUnsignedByte();
                    int len = in.readInt();
                    lastPacketNs = System.nanoTime();
                    bytesIn.addAndGet(len + 5);
                    if (!heardFromPc) {
                        heardFromPc = true;
                        lostConnection = false;
                        showStatus("Connected  ·  starting display…");
                    }
                    if (len < 0 || len > (64 << 20)) throw new IllegalStateException("bad packet");
                    if (type == MSG_CONFIG) {
                        int codecId = in.readUnsignedByte();
                        int w = in.readInt(), h = in.readInt(), fps = in.readInt();
                        byte[] csd = new byte[len - 13];
                        in.readFully(csd);
                        setupCodec(codecId, w, h, fps, csd);
                    } else if (type == MSG_FRAME) {
                        in.readUnsignedByte(); // flags
                        long pts = in.readLong();
                        int n = len - 9;
                        if (frame.length < n) frame = new byte[n + n / 2];
                        in.readFully(frame, 0, n);
                        feed(frame, n, pts);
                    } else if (type == MSG_CURSOR_SHAPE && len >= 4) {
                        final int w = in.readUnsignedShort(), h = in.readUnsignedShort();
                        byte[] px = new byte[len - 4];
                        in.readFully(px);
                        if (w > 0 && h > 0 && px.length >= w * h * 4) {
                            final Bitmap bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
                            bmp.copyPixelsFromBuffer(ByteBuffer.wrap(px, 0, w * h * 4));
                            ui.post(() -> {
                                cursorW = w; cursorH = h;
                                cursorView.setImageBitmap(bmp);
                                applyCursor();
                            });
                        }
                    } else if (type == MSG_CURSOR_POS && len == 9) {
                        cursorVisible = in.readUnsignedByte() != 0;
                        cursorX = in.readInt();
                        cursorY = in.readInt();
                        if (cursorUpdatePending.compareAndSet(false, true)) ui.postAtFrontOfQueue(cursorUpdate);
                    } else {
                        int left = len;
                        while (left > 0) { int k = in.skipBytes(left); if (k <= 0) throw new java.io.EOFException(); left -= k; }
                    }
                }
            } catch (Exception e) {
                // PC not there yet, or it went away: fall through and retry
                if (streaming) lostConnection = true;
            } finally {
                closeSocket();
                if (sender != null) sender.interrupt();
                releaseCodec();
            }
            cursorVisible = false;
            ui.post(() -> { applyCursor(); ui.removeCallbacks(hideBanner); banner.setVisibility(View.GONE); });
            if (running) {
                showStatus(lostConnection
                        ? "Connection to the PC was lost\nReconnecting automatically \u2013 check the USB cable and that SideScreen is still running"
                        : "SideScreen\nWaiting for the PC  \u00b7  press Start in SideScreen on Windows");
                streaming = false;
                try { Thread.sleep(400); } catch (InterruptedException ignored) { }
            }
        }
    }

    /** Once a second: detect a dead link (frozen picture) and refresh the stats overlay. */
    private final Runnable tick = new Runnable() {
        long lastBytes, lastT = System.nanoTime();
        @Override public void run() {
            long now = System.nanoTime();
            if (socket != null && lastPacketNs != 0 && now - lastPacketNs > DEAD_AFTER_NS) {
                lostConnection = true;
                closeSocket(); // the network thread notices, cleans up and reconnects
            }
            double secs = Math.max(0.001, (now - lastT) / 1e9);
            long b = bytesIn.get();
            int frames = framesShown.getAndSet(0);
            long dec = decodeNsSum.getAndSet(0);
            if (statsOn) {
                statsView.setText(String.format(java.util.Locale.US,
                        "%d \u00d7 %d  @ %d Hz\n%5.0f fps shown\n%5.1f Mbps\n%5.1f ms decode\n%s",
                        videoW, videoH, videoFps, frames / secs, (b - lastBytes) * 8 / secs / 1e6,
                        frames > 0 ? dec / 1e6 / frames : 0.0,
                        socket != null ? "USB link OK" : "not connected"));
                statsView.setVisibility(View.VISIBLE);
            } else statsView.setVisibility(View.GONE);
            lastBytes = b;
            lastT = now;
            ui.postDelayed(this, 1000);
        }
    };

    private final Runnable hideBanner = () ->
            banner.animate().alpha(0f).setDuration(600).withEndAction(() -> banner.setVisibility(View.GONE));

    private void showBanner() {
        ui.post(() -> {
            banner.setText("Connected to your PC  \u00b7  " + videoW + " \u00d7 " + videoH + "  \u00b7  " + videoFps + " Hz\n"
                    + "This is now a Windows display \u2013 move the mouse or drag a window over here");
            banner.animate().cancel();
            banner.setAlpha(1f);
            banner.setVisibility(View.VISIBLE);
            ui.removeCallbacks(hideBanner);
            ui.postDelayed(hideBanner, 9000);
        });
    }

    // ------------------------------------------------------------------ pointer overlay

    private final Runnable cursorUpdate = () -> {
        cursorUpdatePending.set(false);
        applyCursor();
    };

    private void applyCursor() {
        int vw = videoW, vh = videoH, sw = surfaceView.getWidth(), sh = surfaceView.getHeight();
        if (!cursorVisible || cursorW == 0 || vw == 0 || sw == 0) {
            cursorView.setVisibility(View.GONE);
            return;
        }
        float sx = sw / (float) vw, sy = sh / (float) vh;
        int w = Math.max(1, Math.round(cursorW * sx)), h = Math.max(1, Math.round(cursorH * sy));
        FrameLayout.LayoutParams p = (FrameLayout.LayoutParams) cursorView.getLayoutParams();
        if (p.width != w || p.height != h) { p.width = w; p.height = h; cursorView.setLayoutParams(p); }
        cursorView.setTranslationX(surfaceView.getLeft() + cursorX * sx);
        cursorView.setTranslationY(surfaceView.getTop() + cursorY * sy);
        cursorView.setVisibility(View.VISIBLE);
    }

    // ------------------------------------------------------------------ decoder

    private void setupCodec(int codecId, final int w, final int h, int fps, byte[] csd) throws Exception {
        releaseCodec();
        Surface out = surface;
        if (out == null) throw new IllegalStateException("no surface");

        String mime = codecId == 2 ? MediaFormat.MIMETYPE_VIDEO_HEVC : MediaFormat.MIMETYPE_VIDEO_AVC;
        MediaFormat f = MediaFormat.createVideoFormat(mime, w, h);
        f.setByteBuffer("csd-0", ByteBuffer.wrap(csd));
        f.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 8 << 20);
        f.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        f.setInteger(MediaFormat.KEY_OPERATING_RATE, fps);
        f.setInteger(MediaFormat.KEY_PRIORITY, 0);          // realtime
        f.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);       // no reorder / no internal queueing
        f.setInteger("allow-frame-drop", 0);                // show every frame, in order
        f.setInteger("vendor.qti-ext-dec-low-latency.enable", 1);
        f.setInteger("vendor.qti-ext-dec-picture-order.enable", 1);

        MediaCodec c = MediaCodec.createDecoderByType(mime);
        c.configure(f, out, null, 0);
        c.start();
        codec = c;
        codecAlive = true;

        outputThread = new Thread(() -> drain(c), "decoder-out");
        outputThread.start();

        videoW = w;
        videoH = h;
        videoFps = fps;
        // letterbox when the PC display has a different aspect ratio than the panel
        ui.post(() -> {
            int rw = root.getWidth(), rh = root.getHeight();
            if (rw == 0 || rh == 0) return;
            float scale = Math.min(rw / (float) w, rh / (float) h);
            FrameLayout.LayoutParams p = (FrameLayout.LayoutParams) surfaceView.getLayoutParams();
            int vw = Math.round(w * scale), vh = Math.round(h * scale);
            p.width = Math.abs(vw - rw) <= 2 ? FrameLayout.LayoutParams.MATCH_PARENT : vw;
            p.height = Math.abs(vh - rh) <= 2 ? FrameLayout.LayoutParams.MATCH_PARENT : vh;
            surfaceView.setLayoutParams(p);
            surfaceView.post(this::applyCursor);
        });
    }

    // lightweight diagnostics (logcat tag "SideScreen")
    private volatile int statIn, statOut, statDelayed, statSkipped;
    private volatile long statMaxInGapNs, statMaxOutGapNs, lastInNs, lastOutNs, statT;

    private void feed(byte[] data, int n, long ptsUs) {
        MediaCodec c = codec;
        if (c == null) return;
        long t = System.nanoTime();
        if (lastInNs != 0) statMaxInGapNs = Math.max(statMaxInGapNs, t - lastInNs);
        lastInNs = t;
        statIn++;
        if (queuedAt.size() < 256) queuedAt.put(ptsUs, t);
        while (running && codecAlive) {
            int idx = c.dequeueInputBuffer(4000);
            if (idx < 0) continue;
            ByteBuffer b = c.getInputBuffer(idx);
            if (b == null || b.capacity() < n) { c.queueInputBuffer(idx, 0, 0, ptsUs, 0); return; }
            b.clear();
            b.put(data, 0, n);
            c.queueInputBuffer(idx, 0, n, ptsUs, 0);
            return;
        }
    }

    private void drain(MediaCodec c) {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_DISPLAY);
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        boolean first = true;
        try {
            while (codecAlive) {
                int idx = c.dequeueOutputBuffer(info, 10000);
                if (idx >= 0) {
                    // Present on the next panel refresh. Frame dropping is disabled on the codec
                    // ("allow-frame-drop" = 0), so frames that arrive in a small USB burst are shown on
                    // consecutive refreshes instead of the older one being silently discarded.
                    long now = System.nanoTime();
                    statOut++;
                    if (lastOutNs != 0) statMaxOutGapNs = Math.max(statMaxOutGapNs, now - lastOutNs);
                    lastOutNs = now;
                    if (now - statT > 2_000_000_000L) {
                        android.util.Log.i("SideScreen", "in=" + statIn + " out=" + statOut + " delayed=" + statDelayed
                                + " skipped=" + statSkipped + " maxInGapMs=" + statMaxInGapNs / 1000000.0
                                + " maxOutGapMs=" + statMaxOutGapNs / 1000000.0 + " over " + (now - statT) / 1000000 + " ms");
                        statT = now; statIn = statOut = statDelayed = statSkipped = 0; statMaxInGapNs = statMaxOutGapNs = 0;
                    }
                    c.releaseOutputBuffer(idx, now);
                    framesShown.incrementAndGet();
                    Long q = queuedAt.remove(info.presentationTimeUs);
                    if (q != null) decodeNsSum.addAndGet(now - q);
                    if (first) { first = false; streaming = true; showStatus(null); showBanner(); }
                }
            }
        } catch (Exception ignored) { }
    }

    private synchronized void releaseCodec() {
        codecAlive = false;
        queuedAt.clear();
        MediaCodec c = codec;
        codec = null;
        Thread t = outputThread;
        outputThread = null;
        if (t != null && t != Thread.currentThread()) {
            try { t.join(500); } catch (InterruptedException ignored) { }
        }
        if (c != null) {
            try { c.stop(); } catch (Exception ignored) { }
            try { c.release(); } catch (Exception ignored) { }
        }
    }

    // ------------------------------------------------------------------ touch -> PC

    private boolean onVideoTouch(View v, MotionEvent e) {
        if (socket == null) return true;
        int action = e.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) v.requestUnbufferedDispatch(e);
        // three-finger tap (quick, all fingers lifted within 350 ms) toggles the stats overlay
        if (action == MotionEvent.ACTION_POINTER_DOWN && e.getPointerCount() == 3) threeFingerDownAt = e.getEventTime();
        else if (action == MotionEvent.ACTION_UP && threeFingerDownAt != 0) {
            if (e.getEventTime() - threeFingerDownAt < 350) statsOn = !statsOn;
            threeFingerDownAt = 0;
            ui.removeCallbacks(tick);
            ui.post(tick);
        } else if (action == MotionEvent.ACTION_POINTER_DOWN && e.getPointerCount() > 3) threeFingerDownAt = 0;
        int count = Math.min(e.getPointerCount(), 10);
        int changed = e.getActionIndex();
        float vw = Math.max(1, v.getWidth() - 1), vh = Math.max(1, v.getHeight() - 1);

        ByteBuffer b = ByteBuffer.allocate(5 + 1 + count * 6);
        b.put((byte) MSG_TOUCH).putInt(1 + count * 6).put((byte) count);
        for (int i = 0; i < count; i++) {
            int state = 1; // move
            if (action == MotionEvent.ACTION_CANCEL) state = 2;
            else if (i == changed) {
                if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN) state = 0;
                else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP) state = 2;
            }
            int nx = Math.round(Math.max(0f, Math.min(1f, e.getX(i) / vw)) * 65535f);
            int ny = Math.round(Math.max(0f, Math.min(1f, e.getY(i) / vh)) * 65535f);
            b.put((byte) e.getPointerId(i)).put((byte) state).putShort((short) nx).putShort((short) ny);
        }
        sendQueue.offer(b.array());
        return true;
    }
}
