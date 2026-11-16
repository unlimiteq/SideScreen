// sscapture - the hot path of SideScreen.
//
//   Desktop Duplication (event driven: wakes exactly when Windows presents a new frame)
//     -> GPU copy into an encoder texture (never touches system memory)
//     -> NVENC HEVC, ultra-low-latency tuning, no B-frames, infinite GOP
//     -> framed packets on stdout
//
// The mouse pointer is NOT burned into the video. Its shape and position are sent separately so the
// tablet can draw it as an overlay, which keeps pointer motion independent of video encode latency.
//
// stdout packets: [type:1][length:4 BE][payload]
//   0x10 CONFIG  codec:1 (2 = HEVC)  width:4  height:4  fps:4  VPS/SPS/PPS (Annex-B)
//   0x11 FRAME   flags:1 (1 = key)   pts_us:8  access unit (Annex-B)
//   0x12 CURSOR_SHAPE  width:2 height:2  RGBA premultiplied pixels
//   0x13 CURSOR_POS    visible:1  x:4  y:4   (top-left of the shape, in display pixels)
// stderr: human readable errors, and "stat fps=.. mbps=.. enc_ms=.." once per second.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <timeapi.h>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <string>
#include <vector>

#include "third_party/nvEncodeAPI.h"

using Microsoft::WRL::ComPtr;

static HANDLE g_out;

[[noreturn]] static void die(int code, const char* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputc('\n', stderr);
    fflush(stderr);
    ExitProcess(code);
}

static SRWLOCK g_outLock = SRWLOCK_INIT; // video thread and pointer thread both write packets

static void writeAll(const uint8_t* p, size_t n)
{
    AcquireSRWLockExclusive(&g_outLock);
    struct Unlock { ~Unlock() { ReleaseSRWLockExclusive(&g_outLock); } } unlock;
    while (n > 0) {
        DWORD done = 0;
        if (!WriteFile(g_out, p, (DWORD)std::min<size_t>(n, 1 << 20), &done, nullptr) || done == 0)
            ExitProcess(0); // host went away
        p += done;
        n -= done;
    }
}

static thread_local std::vector<uint8_t> g_pkt;

static uint8_t* beginPacket(uint8_t type, size_t payload)
{
    g_pkt.resize(5 + payload);
    g_pkt[0] = type;
    g_pkt[1] = (uint8_t)(payload >> 24);
    g_pkt[2] = (uint8_t)(payload >> 16);
    g_pkt[3] = (uint8_t)(payload >> 8);
    g_pkt[4] = (uint8_t)payload;
    return g_pkt.data() + 5;
}
static void endPacket() { writeAll(g_pkt.data(), g_pkt.size()); }
static void put32(uint8_t* p, uint32_t v) { p[0] = v >> 24; p[1] = v >> 16; p[2] = v >> 8; p[3] = (uint8_t)v; }
static void put64(uint8_t* p, uint64_t v) { put32(p, (uint32_t)(v >> 32)); put32(p + 4, (uint32_t)v); }

static double nowMs()
{
    static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart * 1000.0 / freq.QuadPart;
}

// ------------------------------------------------------------------------------------------ cursor

// The pointer is tracked with GetCursorInfo instead of Desktop Duplication's pointer data: for virtual
// (indirect) displays Windows hands the pointer to the display driver and duplication reports nothing.
static bool sendCursorShape(HCURSOR cur, int& hotX, int& hotY)
{
    ICONINFO ii = {};
    if (!GetIconInfo(cur, &ii)) return false;
    BITMAP bm = {};
    GetObject(ii.hbmColor ? ii.hbmColor : ii.hbmMask, sizeof bm, &bm);
    const int w = bm.bmWidth, h = ii.hbmColor ? bm.bmHeight : bm.bmHeight / 2;
    hotX = (int)ii.xHotspot;
    hotY = (int)ii.yHotspot;
    if (ii.hbmColor) DeleteObject(ii.hbmColor);
    if (ii.hbmMask) DeleteObject(ii.hbmMask);
    if (w <= 0 || h <= 0 || w > 512 || h > 512) return false;

    // Draw the cursor on black and on white; the difference gives exact alpha for every cursor type.
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h * 2; // top-down: upper half black background, lower half white
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    uint32_t* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP dib = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, (void**)&bits, nullptr, 0);
    if (!dib) { DeleteDC(dc); return false; }
    HGDIOBJ old = SelectObject(dc, dib);
    RECT top = { 0, 0, w, h }, bottom = { 0, h, w, h * 2 };
    FillRect(dc, &top, (HBRUSH)GetStockObject(BLACK_BRUSH));
    FillRect(dc, &bottom, (HBRUSH)GetStockObject(WHITE_BRUSH));
    DrawIconEx(dc, 0, 0, cur, w, h, 0, nullptr, DI_NORMAL);
    DrawIconEx(dc, 0, h, cur, w, h, 0, nullptr, DI_NORMAL);
    GdiFlush();

    // px: straight BGRA, kind: 0 transparent, 1 normal, 2 "inverts the screen" (cannot be done remotely)
    std::vector<uint32_t> px((size_t)w * h, 0);
    std::vector<uint8_t> kind((size_t)w * h, 0);
    for (size_t i = 0; i < px.size(); i++) {
        const uint32_t onB = bits[i], onW = bits[i + (size_t)w * h];
        const int bb = onB & 0xFF, bg = (onB >> 8) & 0xFF, br = (onB >> 16) & 0xFF;
        const int wb = onW & 0xFF, wg = (onW >> 8) & 0xFF, wr = (onW >> 16) & 0xFF;
        const int diff = ((wb - bb) + (wg - bg) + (wr - br)) / 3; // 255 = transparent, 0 = opaque, <0 = invert
        if (diff < -64) { kind[i] = 2; continue; }
        const int a = std::clamp(255 - diff, 0, 255);
        if (a == 0) continue;
        const int r = std::min(255, br * 255 / a), g = std::min(255, bg * 255 / a), b = std::min(255, bb * 255 / a);
        px[i] = ((uint32_t)a << 24) | (r << 16) | (g << 8) | b;
        kind[i] = 1;
    }
    SelectObject(dc, old);
    DeleteObject(dib);
    DeleteDC(dc);

    // Inverting pixels (text I-beam etc.) become white with a black outline so they show on any background.
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            size_t i = (size_t)y * w + x;
            if (kind[i] != 2) continue;
            px[i] = 0xFFFFFFFF;
            static const int dx[] = { -1, 1, 0, 0 }, dy[] = { 0, 0, -1, 1 };
            for (int k = 0; k < 4; k++) {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                size_t j = (size_t)ny * w + nx;
                if (kind[j] == 0) { px[j] = 0xFF000000; kind[j] = 3; }
            }
        }
    }

    uint8_t* p = beginPacket(0x12, 4 + (size_t)w * h * 4);
    p[0] = (uint8_t)(w >> 8); p[1] = (uint8_t)w; p[2] = (uint8_t)(h >> 8); p[3] = (uint8_t)h;
    p += 4;
    for (size_t i = 0; i < px.size(); i++) {
        uint32_t c = px[i];
        uint32_t a = c >> 24, r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
        p[i * 4 + 0] = (uint8_t)(r * a / 255);
        p[i * 4 + 1] = (uint8_t)(g * a / 255);
        p[i * 4 + 2] = (uint8_t)(b * a / 255);
        p[i * 4 + 3] = (uint8_t)a;
    }
    endPacket();
    return true;
}

static void sendCursorPos(bool visible, int x, int y)
{
    uint8_t* p = beginPacket(0x13, 9);
    p[0] = visible ? 1 : 0;
    put32(p + 1, (uint32_t)x);
    put32(p + 5, (uint32_t)y);
    endPacket();
}

// ------------------------------------------------------------------------------------------ main

static RECT g_display; // captured display in desktop pixels

static DWORD WINAPI cursorThread(LPVOID)
{
    HCURSOR lastCursor = nullptr;
    int hotX = 0, hotY = 0, lastX = INT_MIN, lastY = INT_MIN;
    bool lastVisible = false, first = true;
    for (;;) {
        CURSORINFO ci = { sizeof ci };
        if (GetCursorInfo(&ci)) {
            const bool inside = ci.ptScreenPos.x >= g_display.left && ci.ptScreenPos.x < g_display.right &&
                                ci.ptScreenPos.y >= g_display.top && ci.ptScreenPos.y < g_display.bottom;
            const bool visible = inside && (ci.flags & CURSOR_SHOWING) && ci.hCursor;
            if (visible && ci.hCursor != lastCursor) {
                if (sendCursorShape(ci.hCursor, hotX, hotY)) lastCursor = ci.hCursor;
            }
            const int x = ci.ptScreenPos.x - g_display.left - hotX, y = ci.ptScreenPos.y - g_display.top - hotY;
            if (first || visible != lastVisible || (visible && (x != lastX || y != lastY))) {
                sendCursorPos(visible, x, y);
                lastVisible = visible; lastX = x; lastY = y; first = false;
            }
        }
        Sleep(lastVisible ? 2 : 8); // ~500 Hz while the pointer is on this display
    }
}

static DWORD WINAPI stdinWatcher(LPVOID)
{
    // The host keeps our stdin open; when it dies or closes it, stop immediately (no orphan process).
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    char b[16];
    DWORD n;
    while (ReadFile(in, b, sizeof b, &n, nullptr) && n > 0) { }
    ExitProcess(0);
}

int main(int argc, char** argv)
{
    int adapterIdx = 0, outputIdx = 0, fps = 120, mbps = 80, preset = 1;
    for (int i = 1; i + 1 < argc; i += 2) {
        std::string k = argv[i];
        int v = atoi(argv[i + 1]);
        if (k == "--adapter") adapterIdx = v;
        else if (k == "--output") outputIdx = v;
        else if (k == "--fps") fps = v;
        else if (k == "--mbps") mbps = v;
        else if (k == "--preset") preset = v;
    }
    fps = std::clamp(fps, 10, 480);
    mbps = std::clamp(mbps, 1, 800);

    g_out = GetStdHandle(STD_OUTPUT_HANDLE);
    // Desktop Duplication wants a DPI-aware caller attached to the desktop that currently has input.
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (HDESK desk = OpenInputDesktop(0, FALSE, GENERIC_ALL)) {
        SetThreadDesktop(desk);
        CloseDesktop(desk);
    }
    // 1 ms system timer. This matters far beyond our own Sleep(): Windows paces a virtual display's
    // vsync off the system timer, so at the default 15.6 ms tick a "120 Hz" virtual monitor only
    // refreshes ~64 times a second. Windows 11 ignores timer requests from processes without a
    // visible window unless they opt out of that throttling, so do that first.
    {
        PROCESS_POWER_THROTTLING_STATE pt = {};
        pt.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
        pt.ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION | PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        pt.StateMask = 0; // 0 = never throttle these
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &pt, sizeof pt);
    }
    timeBeginPeriod(1);
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    CreateThread(nullptr, 0, stdinWatcher, nullptr, 0, nullptr);

    // ---- D3D11 device on the adapter that owns the display ----
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) die(3, "DXGI unavailable");
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(factory->EnumAdapters(adapterIdx, &adapter))) die(3, "graphics adapter %d not found", adapterIdx);
    ComPtr<IDXGIOutput> output0;
    if (FAILED(adapter->EnumOutputs(outputIdx, &output0))) die(3, "display output %d not found", outputIdx);
    ComPtr<IDXGIOutput1> output;
    output0.As(&output);
    {
        DXGI_OUTPUT_DESC od = {};
        output0->GetDesc(&od);
        g_display = od.DesktopCoordinates;
    }

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   levels, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    if (FAILED(hr)) die(3, "D3D11 device creation failed (0x%08lx)", hr);
    {
        ComPtr<IDXGIDevice> dxgiDev;
        if (SUCCEEDED(dev.As(&dxgiDev))) dxgiDev->SetGPUThreadPriority(7); // stay smooth while the GPU is busy
    }

    ComPtr<IDXGIOutputDuplication> dupl;
    for (int attempt = 0; attempt < 20; attempt++) {
        hr = output->DuplicateOutput(dev.Get(), &dupl);
        if (SUCCEEDED(hr)) break;
        Sleep(100); // desktop switch in progress
    }
    if (FAILED(hr)) die(4, "cannot capture this display (0x%08lx)", hr);

    DXGI_OUTDUPL_DESC dd;
    dupl->GetDesc(&dd);
    const uint32_t width = dd.ModeDesc.Width, height = dd.ModeDesc.Height;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = width;
    td.Height = height;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> encTex;
    if (FAILED(dev->CreateTexture2D(&td, nullptr, &encTex))) die(3, "texture allocation failed");

    // ---- NVENC ----
    HMODULE lib = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!lib) die(5, "NVIDIA encoder (nvEncodeAPI64.dll) not found");
    typedef NVENCSTATUS(NVENCAPI * CreateInstanceFn)(NV_ENCODE_API_FUNCTION_LIST*);
    auto createInstance = (CreateInstanceFn)GetProcAddress(lib, "NvEncodeAPICreateInstance");
    NV_ENCODE_API_FUNCTION_LIST nv = { NV_ENCODE_API_FUNCTION_LIST_VER };
    if (!createInstance || createInstance(&nv) != NV_ENC_SUCCESS) die(5, "NVENC init failed");

    void* enc = nullptr;
    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS sp = { NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER };
    sp.device = dev.Get();
    sp.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    sp.apiVersion = NVENCAPI_VERSION;
    NVENCSTATUS st = nv.nvEncOpenEncodeSessionEx(&sp, &enc);
    if (st != NV_ENC_SUCCESS) die(5, "NVENC session failed (%d) - is this display on the NVIDIA GPU?", (int)st);

    static const GUID presets[] = { NV_ENC_PRESET_P1_GUID, NV_ENC_PRESET_P2_GUID, NV_ENC_PRESET_P3_GUID,
                                    NV_ENC_PRESET_P4_GUID, NV_ENC_PRESET_P5_GUID };
    const GUID presetGuid = presets[std::clamp(preset, 1, 5) - 1];

    NV_ENC_PRESET_CONFIG pc = { NV_ENC_PRESET_CONFIG_VER, { NV_ENC_CONFIG_VER } };
    st = nv.nvEncGetEncodePresetConfigEx(enc, NV_ENC_CODEC_HEVC_GUID, presetGuid, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, &pc);
    if (st != NV_ENC_SUCCESS) die(5, "NVENC preset query failed (%d)", (int)st);

    NV_ENC_CONFIG cfg = pc.presetCfg;
    cfg.gopLength = NVENC_INFINITE_GOPLENGTH;
    cfg.frameIntervalP = 1;
    cfg.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    cfg.rcParams.averageBitRate = (uint32_t)mbps * 1000000u;
    cfg.rcParams.maxBitRate = (uint32_t)mbps * 1000000u;
    cfg.rcParams.vbvBufferSize = (uint32_t)mbps * 1000000u / 8; // USB is not the bottleneck: allow big frames
    cfg.rcParams.vbvInitialDelay = cfg.rcParams.vbvBufferSize;
    cfg.rcParams.enableAQ = 1;       // spends bits on flat/text regions - sharper desktop content
    cfg.rcParams.zeroReorderDelay = 1;
    auto& hevc = cfg.encodeCodecConfig.hevcConfig;
    hevc.idrPeriod = NVENC_INFINITE_GOPLENGTH;
    hevc.repeatSPSPPS = 1;
    hevc.chromaFormatIDC = 1;
    // NVENC converts BGRA input with BT.601 limited range; tell the decoder so colours are exact.
    auto& vui = hevc.hevcVUIParameters;
    vui.videoSignalTypePresentFlag = 1;
    vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
    vui.videoFullRangeFlag = 0;
    vui.colourDescriptionPresentFlag = 1;
    vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
    vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_SRGB;
    vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT470BG;

    NV_ENC_INITIALIZE_PARAMS ip = { NV_ENC_INITIALIZE_PARAMS_VER };
    ip.encodeGUID = NV_ENC_CODEC_HEVC_GUID;
    ip.presetGUID = presetGuid;
    ip.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
    ip.encodeWidth = ip.darWidth = ip.maxEncodeWidth = width;
    ip.encodeHeight = ip.darHeight = ip.maxEncodeHeight = height;
    ip.frameRateNum = (uint32_t)fps;
    ip.frameRateDen = 1;
    ip.enablePTD = 1;
    ip.encodeConfig = &cfg;
    st = nv.nvEncInitializeEncoder(enc, &ip);
    if (st != NV_ENC_SUCCESS) die(5, "NVENC cannot encode %ux%u@%d HEVC (%d): %s", width, height, fps, (int)st, nv.nvEncGetLastErrorString(enc));

    NV_ENC_CREATE_BITSTREAM_BUFFER bb = { NV_ENC_CREATE_BITSTREAM_BUFFER_VER };
    if (nv.nvEncCreateBitstreamBuffer(enc, &bb) != NV_ENC_SUCCESS) die(5, "NVENC bitstream buffer failed");

    NV_ENC_REGISTER_RESOURCE rr = { NV_ENC_REGISTER_RESOURCE_VER };
    rr.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
    rr.resourceToRegister = encTex.Get();
    rr.width = width;
    rr.height = height;
    rr.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
    rr.bufferUsage = NV_ENC_INPUT_IMAGE;
    if (nv.nvEncRegisterResource(enc, &rr) != NV_ENC_SUCCESS) die(5, "NVENC texture registration failed: %s", nv.nvEncGetLastErrorString(enc));

    // ---- stream header ----
    {
        uint8_t hdr[1024];
        uint32_t hdrSize = 0;
        NV_ENC_SEQUENCE_PARAM_PAYLOAD sq = { NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER };
        sq.inBufferSize = sizeof hdr;
        sq.spsppsBuffer = hdr;
        sq.outSPSPPSPayloadSize = &hdrSize;
        if (nv.nvEncGetSequenceParams(enc, &sq) != NV_ENC_SUCCESS) hdrSize = 0;
        uint8_t* p = beginPacket(0x10, 13 + hdrSize);
        p[0] = 2;
        put32(p + 1, width);
        put32(p + 5, height);
        put32(p + 9, (uint32_t)fps);
        memcpy(p + 13, hdr, hdrSize);
        endPacket();
    }

    CreateThread(nullptr, 0, cursorThread, nullptr, 0, nullptr);

    // ---- capture / encode loop ----
    const double frameMs = 1000.0 / fps;
    const double minGapMs = frameMs - 0.02;   // feed the tablet exactly its refresh rate, never more
    const int refineFrames = 6;               // after motion stops, re-encode the still image to full quality
    const double t0 = nowMs();
    double lastEncode = -1e9, statT = t0, encMsSum = 0;
    int statFrames = 0, refine = 0;
    uint64_t statBytes = 0, frameNo = 0;
    bool haveImage = false;

    auto encode = [&](bool forceKey) {
        double a = nowMs();
        NV_ENC_MAP_INPUT_RESOURCE mr = { NV_ENC_MAP_INPUT_RESOURCE_VER };
        mr.registeredResource = rr.registeredResource;
        if (nv.nvEncMapInputResource(enc, &mr) != NV_ENC_SUCCESS) die(6, "NVENC map failed: %s", nv.nvEncGetLastErrorString(enc));

        NV_ENC_PIC_PARAMS pp = { NV_ENC_PIC_PARAMS_VER };
        pp.inputBuffer = mr.mappedResource;
        pp.bufferFmt = mr.mappedBufferFmt;
        pp.inputWidth = width;
        pp.inputHeight = height;
        pp.outputBitstream = bb.bitstreamBuffer;
        pp.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
        pp.inputTimeStamp = frameNo++;
        if (forceKey) pp.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
        st = nv.nvEncEncodePicture(enc, &pp);
        if (st != NV_ENC_SUCCESS) die(6, "NVENC encode failed (%d): %s", (int)st, nv.nvEncGetLastErrorString(enc));

        NV_ENC_LOCK_BITSTREAM lb = { NV_ENC_LOCK_BITSTREAM_VER };
        lb.outputBitstream = bb.bitstreamBuffer;
        if (nv.nvEncLockBitstream(enc, &lb) != NV_ENC_SUCCESS) die(6, "NVENC readback failed: %s", nv.nvEncGetLastErrorString(enc));
        const bool key = lb.pictureType == NV_ENC_PIC_TYPE_IDR || lb.pictureType == NV_ENC_PIC_TYPE_I;
        uint8_t* p = beginPacket(0x11, 9 + lb.bitstreamSizeInBytes);
        p[0] = key ? 1 : 0;
        put64(p + 1, (uint64_t)((a - t0) * 1000.0));
        memcpy(p + 9, lb.bitstreamBufferPtr, lb.bitstreamSizeInBytes);
        statBytes += lb.bitstreamSizeInBytes;
        nv.nvEncUnlockBitstream(enc, bb.bitstreamBuffer);
        nv.nvEncUnmapInputResource(enc, mr.mappedResource);
        encMsSum += nowMs() - a;
        endPacket();
        lastEncode = a;
        statFrames++;
    };

    for (;;) {
        // Pace: never faster than the tablet can show. Windows composes a virtual display at the rate
        // of the fastest real monitor (e.g. 240 Hz), so without this we would overfeed the decoder.
        // Sleep covers the bulk; the last fraction of a millisecond is a short spin for precision.
        const double due = lastEncode + minGapMs;
        double wait = due - nowMs();
        if (wait > 1.3) Sleep((DWORD)(wait - 1.0));
        while (nowMs() < due) YieldProcessor();

        DXGI_OUTDUPL_FRAME_INFO fi = {};
        ComPtr<IDXGIResource> res;
        hr = dupl->AcquireNextFrame(refine > 0 ? (UINT)(frameMs * 2 + 1) : 250, &fi, &res);

        if (hr == DXGI_ERROR_WAIT_TIMEOUT) {
            if (haveImage && refine > 0) { refine--; encode(false); }
        } else if (FAILED(hr)) {
            // mode change, secure desktop (UAC / lock screen), driver reset: the host simply restarts us
            die(10, "display changed (0x%08lx)", hr);
        } else {
            const bool newImage = fi.LastPresentTime.QuadPart != 0 && res;
            if (newImage) {
                ComPtr<ID3D11Texture2D> tex;
                if (SUCCEEDED(res.As(&tex))) ctx->CopyResource(encTex.Get(), tex.Get());
            }
            res.Reset();
            dupl->ReleaseFrame();
            if (newImage) {
                encode(!haveImage);
                haveImage = true;
                refine = refineFrames;
            }
        }

        double t = nowMs();
        if (t - statT >= 1000.0) {
            double s = (t - statT) / 1000.0;
            fprintf(stderr, "stat fps=%.1f mbps=%.2f enc_ms=%.2f\n", statFrames / s, statBytes * 8 / s / 1e6,
                    statFrames ? encMsSum / statFrames : 0.0);
            fflush(stderr);
            statT = t; statFrames = 0; statBytes = 0; encMsSum = 0;
        }
    }
}
