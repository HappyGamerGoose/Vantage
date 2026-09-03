// SPDX-License-Identifier: MIT
// Vantage — WindowsAutomationService.cs
//
// Capability-only scaffold for native Win32 input injection and screen capture.
// NO LLM call. NO conversation state. NO consent logic.
//
// CONSENT, AUDIT, AND GATING ARE THE CALLER'S RESPONSIBILITY.
// This service will happily inject clicks and capture pixels without checking
// whether the user agreed. Vantage calls it only from a user-initiated run
// with a visible running state, immediate Stop/Escape cancellation, action
// history, and deterministic safety gates around sensitive operations.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.System;

namespace Vantage.Services;

public sealed class WindowsAutomationService
{
    private const int InputSettleDelayMs = 1;
    private const int DragStartDelayMs = 8;
    private const int DragStepDelayMs = 4;
    // ── P/Invokes ────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // ── Win32 Clipboard ───────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    // ── Synthetic-input marker ─────────────────────────────────
    // Set every time this service injects mouse / keyboard / clipboard
    // input, so the panic monitor can distinguish "the agent moved the
    // cursor to click something" from "the human is frantically waving
    // the mouse to abort". Polled by RunPanicMonitorAsync — see
    // TimeSinceLastSyntheticInput.
    private static DateTime _lastSyntheticInputAt = DateTime.MinValue;

    /// <summary>UTC instant of the last synthetic input we injected.</summary>
    public static DateTime LastSyntheticInputAt => _lastSyntheticInputAt;

    /// <summary>
    /// Time elapsed since the last synthetic input. The panic monitor
    /// treats a non-zero recent value as "the agent is the one moving
    /// the cursor, ignore the motion".
    /// </summary>
    public static TimeSpan TimeSinceLastSyntheticInput =>
        DateTime.UtcNow - _lastSyntheticInputAt;

    private static void MarkSyntheticInput() =>
        _lastSyntheticInputAt = DateTime.UtcNow;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MDT_EFFECTIVE_DPI         = 0;
    private const int  ENUM_CURRENT_SETTINGS     = -1;

    /// <summary>
    /// Win32 DEVMODE layout. Field order and sizes must match the
    /// unmanaged struct exactly; dmSize is set to Marshal.SizeOf&lt;DEVMODE&gt;
    /// before calling EnumDisplaySettings so the API knows how much buffer
    /// to fill.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME   = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;

        public uint   dmFields;

        public int    dmPositionX;
        public int    dmPositionY;
        public uint   dmDisplayOrientation;
        public uint   dmDisplayFixedOutput;

        public short  dmColor;
        public short  dmDuplex;
        public short  dmYResolution;
        public short  dmTTOption;
        public short  dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint   dmBitsPerPel;
        public uint   dmPelsWidth;     // ← ground-truth hardware width
        public uint   dmPelsHeight;    // ← ground-truth hardware height
        public uint   dmDisplayFlags;
        public uint   dmDisplayFrequency;
        public uint   dmICMMethod;
        public uint   dmICMIntent;
        public uint   dmMediaType;
        public uint   dmDitherType;
        public uint   dmReserved1;
        public uint   dmReserved2;
        public uint   dmPanningFlags;
        public uint   dmDisplayMode;
        public uint   dmLogPixelsX;
        public uint   dmLogPixelsY;
    }

    // ── Constants ────────────────────────────────────────────────
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY    = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;

    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const int VK_ESCAPE  = 0x1B;
    private const int VK_LSHIFT  = 0xA0;
    private const int VK_RSHIFT  = 0xA1;
    private const int VK_LCONTROL= 0xA2;
    private const int VK_RCONTROL= 0xA3;
    private const int VK_LMENU   = 0xA4;

    // ── Structs ──────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTDATA Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTDATA
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private static int InputSize => Marshal.SizeOf<INPUT>();

    // ── Monitor geometry / DPI ──────────────────────────────────

    /// <summary>
    /// Description of the primary monitor for both capture (physical
    /// pixels) and tooling (logical pixels = Windows DIPs).
    /// </summary>
    public sealed record MonitorGeometry(
        int PhysicalWidth,
        int PhysicalHeight,
        int Dpi,
        int LogicalWidth,
        int LogicalHeight)
    {
        public double PhysicalToLogicalScale => Dpi / 96.0;
        public double LogicalToPhysicalScale => Dpi / 96.0;
    }

    /// <summary>
    /// Returns the primary monitor's ground-truth hardware pixels,
    /// effective DPI, and the derived logical-pixel frame the model
    /// reasons in. We deliberately bypass <c>GetSystemMetrics</c> for the
    /// physical size because on PerMonitorV2-aware processes the value it
    /// returns depends on the thread's DPI context and is NOT guaranteed
    /// to be the LCD's raw hardware resolution — using it would push
    /// double-scale error into every cursor mapping and miss targets on
    /// high-DPI laptop displays. EnumDisplaySettings / DEVMODE gives the
    /// actual panel pixels and is what BitBlt ultimately draws against.
    /// </summary>
    public static MonitorGeometry GetPrimaryMonitor()
    {
        // 1. Effective DPI of the primary monitor via shcore (per-monitor aware).
        IntPtr hmon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        int dpi = 96;
        if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            dpi = (int)dpiX;

        // 2. Ground-truth hardware pixels via EnumDisplaySettings.
        //    ENUM_CURRENT_SETTINGS = -1 ("give me what the panel is currently
        //    running at"). DEVMODE.dmPelsWidth/Height are the LCD's raw pixels,
        //    not DPI-scaled.
        int physW = 0;
        int physH = 0;
        var dev = new DEVMODE();
        try
        {
            dev.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dev))
            {
                physW = (int)dev.dmPelsWidth;
                physH = (int)dev.dmPelsHeight;
            }
        }
        catch
        {
            // Some headless test hosts can throw on this call. Fall through.
        }

        // 3. Belt-and-braces fallback. Only fires on exotic machines.
        if (physW <= 0 || physH <= 0)
        {
            physW = GetSystemMetrics(SM_CXSCREEN);
            physH = GetSystemMetrics(SM_CYSCREEN);
        }

        // 4. Derive logical (= DIP) dimensions from the raw hardware pixels
        //    using the active DPI. This is the frame the model reasons in.
        int scale   = Math.Max(dpi, 1);
        int logicalW = physW * 96 / scale;
        int logicalH = physH * 96 / scale;

        return new MonitorGeometry(physW, physH, dpi, logicalW, logicalH);
    }

    /// <summary>
    /// Logical-Windows DIP → physical pixel coordinate (the units
    /// SetCursorPos expects). Rounded to the nearest pixel.
    /// </summary>
    public static (int X, int Y) LogicalToPhysical(int logicalX, int logicalY)
    {
        var geo = GetPrimaryMonitor();
        return (
            (int)Math.Round(logicalX * geo.LogicalToPhysicalScale),
            (int)Math.Round(logicalY * geo.LogicalToPhysicalScale));
    }

    private static INPUT MakeMouseInput(uint flags, int dx, int dy, uint mouseData = 0) => new()
    {
        Type = INPUT_MOUSE,
        Data = new INPUTDATA
        {
            Mouse = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
        }
    };

    private static INPUT MakeVkKey(ushort vKey, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new INPUTDATA
        {
            Keyboard = new KEYBDINPUT
            {
                wVk = vKey,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static INPUT MakeUnicodeKey(int codepoint, bool keyUp, out ushort scan)
    {
        // SendInput accepts UTF-16 in wScan with KEYEVENTF_UNICODE.
        // For codepoints in the BMP we pack the low 16 bits directly.
        scan = (ushort)(codepoint & 0xFFFF);
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Data = new INPUTDATA
            {
                Keyboard = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    // ── Screen capture ───────────────────────────────────────────

    /// <summary>
    /// Captures the entire primary display, downscales to <c>captureWidth</c>
    /// image pixels wide while preserving the monitor's native aspect ratio
    /// (16:9, 16:10, etc.) — never forces a 4:3 squish. Pass through DPI math
    /// so PerMonitorV2 scaling is honored on 4K@200% / 1440p@125% displays.
    /// </summary>
    /// <param name="captureWidth">Output width in pixels.</param>
    /// <param name="quality">JPEG quality 0–100.</param>
    /// <returns>JPEG bytes; resolution = <c>captureWidth × round(captureWidth × logicalH / logicalW)</c>.</returns>
    public static byte[] CaptureScreenJpeg(int captureWidth = 1024, long quality = 72)
    {
        if (captureWidth <= 0) throw new ArgumentOutOfRangeException(nameof(captureWidth));
        if (quality < 0 || quality > 100) quality = 72;

        var geo = GetPrimaryMonitor();
        if (geo.PhysicalWidth <= 0 || geo.PhysicalHeight <= 0)
            throw new InvalidOperationException("No primary display detected.");

        // Aspect-preserving target: figure out the logical height that matches
        // the monitor's natural ratio at the requested width.
        int logicalDestW = captureWidth;
        int logicalDestH = (int)Math.Round(captureWidth * (double)geo.LogicalHeight / geo.LogicalWidth);

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc   = IntPtr.Zero;
        IntPtr bmp      = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc    = CreateCompatibleDC(screenDc);
            bmp      = CreateCompatibleBitmap(screenDc, geo.PhysicalWidth, geo.PhysicalHeight);
            SelectObject(memDc, bmp);
            BitBlt(memDc, 0, 0, geo.PhysicalWidth, geo.PhysicalHeight, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);

            using var full = Image.FromHbitmap(bmp);
            full.SetResolution(geo.Dpi, geo.Dpi);
            // Bitmap dimensions, not DPI metadata, determine verifier cost.
            // Keep the requested cap exact on high-DPI displays.
            using var resized = new Bitmap(logicalDestW, logicalDestH, PixelFormat.Format32bppArgb);
            resized.SetResolution(96, 96);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode     = SmoothingMode.HighQuality;
                g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                g.CompositingQuality= CompositingQuality.HighQuality;
                g.DrawImage(full, 0, 0, logicalDestW, logicalDestH);
            }

            var jpeg = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                (long)Math.Clamp(quality, 0L, 100L));

            using var ms = new MemoryStream();
            resized.Save(ms, jpeg, p);
            return ms.ToArray();
        }
        finally
        {
            if (bmp      != IntPtr.Zero) DeleteObject(bmp);
            if (memDc    != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Same as <see cref="CaptureScreenJpeg(int,long)"/> but Base64-encoded,
    /// ready to embed in an Anthropic `image` content block. Typical payload
    /// ~25–60 KB at 1024 logical pixels wide @ quality 72.
    /// </summary>
    public static string CaptureScreenJpegAsBase64(int captureWidth = 1024, long quality = 72) =>
        Convert.ToBase64String(CaptureScreenJpeg(captureWidth, quality));

    /// <summary>
    /// Capture the entire primary monitor at its NATIVE PHYSICAL RESOLUTION
    /// and return the result as lossless PNG bytes — no downsize, no JPEG
    /// quality loss. Use this when the model needs every pixel (e.g. small
    /// text or fine UI details the model can't read at compressed JPEG).
    ///
    /// Typical payload is large: 1080p ≈ 3-7 MB, 1440p ≈ 5-12 MB, 4K ≈
    /// 15-40 MB. Confirm your model's per-request image budget before
    /// piping this into a per-step request.
    /// </summary>
    public static byte[] CaptureScreenPngFullResolution()
    {
        var geo = GetPrimaryMonitor();
        if (geo.PhysicalWidth <= 0 || geo.PhysicalHeight <= 0)
            throw new InvalidOperationException("No primary display detected.");

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc    = CreateCompatibleDC(screenDc);
            bmp      = CreateCompatibleBitmap(screenDc, geo.PhysicalWidth, geo.PhysicalHeight);
            SelectObject(memDc, bmp);
            // CAPTUREBLT ensures layered / on-screen-rendered surfaces are
            // included (e.g. text-renderer DPI ramp, IME, cursor-with-effects).
            BitBlt(memDc, 0, 0, geo.PhysicalWidth, geo.PhysicalHeight, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);

            using var full = Image.FromHbitmap(bmp);
            full.SetResolution(geo.Dpi, geo.Dpi);
            using var ms = new MemoryStream();
            full.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (bmp      != IntPtr.Zero) DeleteObject(bmp);
            if (memDc    != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Base64 wrapper around <see cref="CaptureScreenPngFullResolution"/>.</summary>
    public static string CaptureScreenPngFullResolutionAsBase64() =>
        Convert.ToBase64String(CaptureScreenPngFullResolution());

    /// <summary>
    /// Capture the primary monitor at full physical resolution, then downscale
    /// to a PNG with the given longest-edge pixel cap. Still lossless (PNG, not
    /// JPEG) so fine UI text reads crisply, but the per-screenshot base64 size
    /// is bounded: a 1080p capture capped at 1280 px lands at ~1.5 MB, a 1440p
    /// capture at ~2.5 MB, 4K at ~5 MB. Use this for LLM-facing captures to
    /// stay comfortably under provider request-body budgets (~250 MB on most
    /// upstream proxies / routes) over many steps. Captures pre/post for the
    /// verifier diff can keep using <see cref="CaptureScreenJpeg(int,long)"/>.
    /// </summary>
    public static byte[] CaptureScreenPng(int maxLongestSide = 1280)
    {
        var geo = GetPrimaryMonitor();
        if (geo.PhysicalWidth <= 0 || geo.PhysicalHeight <= 0)
            throw new InvalidOperationException("No primary display detected.");

        int srcW = geo.PhysicalWidth, srcH = geo.PhysicalHeight;
        // Compute target dimensions preserving aspect ratio. If already smaller
        // than the cap, keep source dims (no pointless upscale).
        int dstW, dstH;
        int longest = Math.Max(srcW, srcH);
        if (longest <= maxLongestSide)
        {
            dstW = srcW; dstH = srcH;
        }
        else
        {
            double scale = (double)maxLongestSide / longest;
            dstW = (int)Math.Round(srcW * scale);
            dstH = (int)Math.Round(srcH * scale);
        }

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc    = CreateCompatibleDC(screenDc);
            bmp      = CreateCompatibleBitmap(screenDc, srcW, srcH);
            SelectObject(memDc, bmp);
            // CAPTUREBLT ensures layered / on-screen-rendered surfaces are
            // included (IME, text-renderer DPI ramp, cursor-with-effects).
            BitBlt(memDc, 0, 0, srcW, srcH, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);

            using var full = Image.FromHbitmap(bmp);
            full.SetResolution(geo.Dpi, geo.Dpi);
            using var resized = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            resized.SetResolution(geo.Dpi, geo.Dpi);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingMode    = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode      = SmoothingMode.HighQuality;
                g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, dstW, dstH);
            }
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (bmp      != IntPtr.Zero) DeleteObject(bmp);
            if (memDc    != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Base64 wrapper around <see cref="CaptureScreenPng(int)"/>.</summary>
    public static string CaptureScreenPngAsBase64(int maxLongestSide = 1280) =>
        Convert.ToBase64String(CaptureScreenPng(maxLongestSide));

    /// <summary>
    /// Capture the screen and produce a PNG whose pixel space matches the
    /// monitor's LOGICAL-pixel frame (not physical pixels). At 100% DPI
    /// this is the same as the physical pixel cap; at 125% / 150% scaling
    /// the logical space is the same number of pixels the model reasons
    /// in, so coords from the grounding LLM map 1:1 to logical coords.
    /// This eliminates the screen-vs-image scale mismatch that pulled
    /// every click off-target on high-DPI displays.
    /// </summary>
    public static byte[] CaptureScreenPngLogical(int maxLongestSide = 1280)
    {
        var geo = GetPrimaryMonitor();
        if (geo.PhysicalWidth <= 0 || geo.PhysicalHeight <= 0)
            throw new InvalidOperationException("No primary display detected.");

        // Logical source dims — what the model reasons in.
        int srcLogicalW = geo.LogicalWidth;
        int srcLogicalH = geo.LogicalHeight;

        // Logical target dims — capped at maxLongestSide.
        int longest = Math.Max(srcLogicalW, srcLogicalH);
        int dstLogicalW, dstLogicalH;
        if (longest <= maxLongestSide)
        {
            dstLogicalW = srcLogicalW;
            dstLogicalH = srcLogicalH;
        }
        else
        {
            double scale = (double)maxLongestSide / longest;
            dstLogicalW = (int)Math.Round(srcLogicalW * scale);
            dstLogicalH = (int)Math.Round(srcLogicalH * scale);
        }

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc    = CreateCompatibleDC(screenDc);
            bmp      = CreateCompatibleBitmap(screenDc, geo.PhysicalWidth, geo.PhysicalHeight);
            SelectObject(memDc, bmp);
            BitBlt(memDc, 0, 0, geo.PhysicalWidth, geo.PhysicalHeight, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);

            using var full = Image.FromHbitmap(bmp);
            full.SetResolution(geo.Dpi, geo.Dpi);
            // Bitmap dimensions are the coordinate space a vision model
            // sees. Keep them in logical pixels; DPI metadata does not
            // change a PNG's actual pixel dimensions and previously caused
            // a 1280px cap to expand back to 1920px at 150% scaling.
            using var resized = new Bitmap(dstLogicalW, dstLogicalH, PixelFormat.Format32bppArgb);
            resized.SetResolution(96, 96);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingMode    = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode      = SmoothingMode.HighQuality;
                g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, dstLogicalW, dstLogicalH);
            }
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (bmp      != IntPtr.Zero) DeleteObject(bmp);
            if (memDc    != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Base64 wrapper around <see cref="CaptureScreenPngLogical(int)"/>.</summary>
    public static string CaptureScreenPngLogicalAsBase64(int maxLongestSide = 1280) =>
        Convert.ToBase64String(CaptureScreenPngLogical(maxLongestSide));

    // ── Cursor queries (used by the panic monitor) ───────────────

    /// <summary>
    /// Returns current cursor position in PHYSICAL pixels (Windows owns the
    /// cursor in physical space; SetCursorPos sets it in physical space).
    /// </summary>
    public static (int X, int Y) GetCursorPositionPhysical()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Returns current cursor position in LOGICAL pixels (= the units the
    /// computer-use model reasons in).
    /// </summary>
    public static (int X, int Y) GetCursorPositionLogical()
    {
        var phys = GetCursorPositionPhysical();
        var scale = GetPrimaryMonitor().LogicalToPhysicalScale;
        return (
            (int)Math.Round(phys.X / scale),
            (int)Math.Round(phys.Y / scale));
    }

    /// <summary>
    /// Returns true while the Escape key is physically held down. Polled by
    /// the orchestrator's panic monitor at ~20 Hz. Don't use this as a
    /// single-shot edge detector — it'd race against fast Escape presses.
    /// </summary>
    public static bool IsEscapeHeld() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    // ── Mouse input (all coordinates are LOGICAL pixels) ────────

    public static void MoveMouse(int logicalX, int logicalY)
    {
        var (px, py) = LogicalToPhysical(logicalX, logicalY);
        if (!SetCursorPos(px, py))
            throw new InvalidOperationException($"SetCursorPos failed (logical=({logicalX},{logicalY}) → physical=({px},{py})).");
        MarkSyntheticInput();
    }

    public static void LeftClick(int logicalX, int logicalY)
    {
        MoveMouse(logicalX, logicalY);
        Thread.Sleep(InputSettleDelayMs);
        SendBatch(
            MakeMouseInput(MOUSEEVENTF_LEFTDOWN, 0, 0),
            MakeMouseInput(MOUSEEVENTF_LEFTUP,   0, 0));
        MarkSyntheticInput();
    }

    public static void DoubleLeftClick(int logicalX, int logicalY)
    {
        LeftClick(logicalX, logicalY);
        Thread.Sleep(40);
        LeftClick(logicalX, logicalY);
        MarkSyntheticInput();
    }

    public static void RightClick(int logicalX, int logicalY)
    {
        MoveMouse(logicalX, logicalY);
        Thread.Sleep(InputSettleDelayMs);
        SendBatch(
            MakeMouseInput(MOUSEEVENTF_RIGHTDOWN, 0, 0),
            MakeMouseInput(MOUSEEVENTF_RIGHTUP,   0, 0));
        MarkSyntheticInput();
    }

    public static void MiddleClick(int logicalX, int logicalY)
    {
        MoveMouse(logicalX, logicalY);
        Thread.Sleep(InputSettleDelayMs);
        SendBatch(
            MakeMouseInput(MOUSEEVENTF_MIDDLEDOWN, 0, 0),
            MakeMouseInput(MOUSEEVENTF_MIDDLEUP,   0, 0));
        MarkSyntheticInput();
    }

    public enum MouseButton
    {
        Left,
        Right,
        Middle,
    }

    /// <summary>
    /// Drag between logical coordinates while holding the requested mouse
    /// button. Cancellation always releases the button before propagating.
    /// </summary>
    public static async Task DragAsync(
        int fromLogicalX,
        int fromLogicalY,
        int toLogicalX,
        int toLogicalY,
        MouseButton button,
        CancellationToken ct)
    {
        MoveMouse(fromLogicalX, fromLogicalY);
        await Task.Delay(DragStartDelayMs, ct);

        var (downFlag, upFlag) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        SendBatch(MakeMouseInput(downFlag, 0, 0));
        MarkSyntheticInput();
        try
        {
            const int steps = 12;
            for (var step = 1; step <= steps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var x = (int)Math.Round(fromLogicalX + (toLogicalX - fromLogicalX) * (step / (double)steps));
                var y = (int)Math.Round(fromLogicalY + (toLogicalY - fromLogicalY) * (step / (double)steps));
                MoveMouse(x, y);
                await Task.Delay(DragStepDelayMs, ct);
            }
        }
        finally
        {
            SendBatch(MakeMouseInput(upFlag, 0, 0));
            MarkSyntheticInput();
        }
    }

    /// <summary>
    /// Positive delta scrolls up, negative down. Wheel delta of 120 equals
    /// one notch on a standard mouse wheel. Coordinates are LOGICAL.
    /// </summary>
    public static void Scroll(int delta, int? logicalX = null, int? logicalY = null)
    {
        if (logicalX.HasValue && logicalY.HasValue)
            MoveMouse(logicalX.Value, logicalY.Value);
        SendBatch(MakeMouseInput(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta));
        MarkSyntheticInput();
    }

    // ── Clipboard (CF_UNICODETEXT) ───────────────────────────────
    //
    // The whole point of these methods is to solve the legibility
    // problem caused by downsampling a 3840×2160 panel to 1024 wide:
    // small fonts become ~1 px glyphs and the OCR pipeline inside the
    // model starts guessing. With Ctrl+A → Ctrl+C in the target app +
    // GetClipboardText() the model pulls exact raw text instead of
    // OCR'ing pixels.
    //
    // Thread-safety: OpenClipboard is a process-wide lock. We use a
    // bounded retry because RDP / clipboard-manager apps occasionally
    // hold it for a few ms during a paste operation.

    private const int OpenClipboardMaxAttempts = 5;
    private const int OpenClipboardDelayMs     = 50;

    private static bool TryOpenClipboard()
    {
        for (int i = 0; i < OpenClipboardMaxAttempts; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(OpenClipboardDelayMs);
        }
        return false;
    }

    /// <summary>
    /// Reads Unicode text from the Windows clipboard. Returns null if the
    /// clipboard is empty / unavailable / holds something other than
    /// CF_UNICODETEXT. Retries OpenClipboard a few times before giving up.
    /// </summary>
    public static string? GetClipboardText()
    {
        if (!TryOpenClipboard()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;            // empty, or non-text format
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(p);
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Replaces the Windows clipboard's Unicode text with
    /// <paramref name="text"/>. Allocates via <c>GlobalAlloc(GMEM_MOVEABLE)</c>,
    /// copies UTF-16 + null terminator via <c>Marshal.Copy</c>, and hands
    /// ownership to Windows when <c>SetClipboardData</c> succeeds
    /// (Windows frees the buffer; we must NOT GlobalFree it). On any
    /// failure we GlobalFree it ourselves.
    /// </summary>
    public static bool SetClipboardText(string text)
    {
        if (text is null) text = string.Empty;

        if (!TryOpenClipboard()) return false;
        try
        {
            EmptyClipboard();

            // +1 for the terminating UTF-16 null.
            int charCount = text.Length + 1;
            uint byteCount = (uint)(charCount * 2);

            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
            if (hMem == IntPtr.Zero) return false;

            var p = GlobalLock(hMem);
            if (p == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }

            try
            {
                if (text.Length > 0)
                {
                    // Marshal.Copy of a char[] writes 2 bytes per char; matches UTF-16 LE.
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                }
                // Append the UTF-16 null terminator (2 bytes of zero).
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hMem);
            }

            var placed = SetClipboardData(CF_UNICODETEXT, hMem);
            if (placed == IntPtr.Zero)
            {
                // SetClipboardData rejected the buffer (clipboard closed
                // mid-call, OOM, etc.). We're still the owner.
                GlobalFree(hMem);
                return false;
            }

            // On success the clipboard owns hMem — do NOT free it.
            MarkSyntheticInput();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ── Keyboard input ──────────────────────────────────────────

    /// <summary>
    /// Types <paramref name="text"/> one character at a time, applying the
    /// correct shift / VK mapping for ASCII or Unicode for the rest.
    /// Throws if a SendInput call returns 0 (synth-input rejected, e.g.
    /// focus moved mid-stream).
    /// </summary>
    public static void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
        {
            TypeOneRune(rune);
        }
        MarkSyntheticInput();
    }

    public static Task<int> TypeAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return Task.FromResult(0);
        var typed = 0;
        try
        {
            foreach (var rune in text.EnumerateRunes())
            {
                ct.ThrowIfCancellationRequested();
                TypeOneRune(rune);
                typed++;
            }
            return Task.FromResult(typed);
        }
        finally
        {
            if (typed > 0) MarkSyntheticInput();
        }
    }

    private static void TypeOneRune(Rune rune)
    {
        // Try ASCII shortcut first.
        if (rune.Value < 0x80 && rune.Value > 0x1F)
        {
            short vkScan = VkKeyScan((char)rune.Value);
            if (vkScan != -1)
            {
                var vKey = (ushort)(vkScan & 0xFF);
                var modifierState = (vkScan >> 8) & 0xFF;
                var modifiers = new List<ushort>(3);
                if ((modifierState & 1) != 0) modifiers.Add(VK_LSHIFT);
                if ((modifierState & 2) != 0) modifiers.Add(VK_LCONTROL);
                if ((modifierState & 4) != 0) modifiers.Add(VK_LMENU);

                foreach (var modifier in modifiers) SendBatch(MakeVkKey(modifier, false));
                try
                {
                    PressAndRelease(vKey);
                }
                finally
                {
                    for (var i = modifiers.Count - 1; i >= 0; i--)
                    {
                        SendBatch(MakeVkKey(modifiers[i], true));
                    }
                }
                return;
            }
        }
        // Newline / Tab get their dedicated VKs.
        if (rune.Value == '\n') { PressAndRelease(0x0D); return; }   // VK_RETURN
        if (rune.Value == '\r') { PressAndRelease(0x0D); return; }
        if (rune.Value == '\t') { PressAndRelease(0x09); return; }   // VK_TAB

        // SendInput consumes UTF-16 code units. Supplementary runes need
        // both surrogate halves, each with a down/up pair.
        Span<char> utf16 = stackalloc char[2];
        var units = rune.EncodeToUtf16(utf16);
        for (var i = 0; i < units; i++)
        {
            var down = MakeUnicodeKey(utf16[i], keyUp: false, out _);
            var up = MakeUnicodeKey(utf16[i], keyUp: true, out _);
            SendBatch(down, up);
        }
    }

    private static void PressAndRelease(ushort vKey)
    {
        SendBatch(MakeVkKey(vKey, false), MakeVkKey(vKey, true));
    }

    private static void PressAndRelease(int vKey) => PressAndRelease((ushort)vKey);

    public static void SendKey(VirtualKey vk)
    {
        PressAndRelease((ushort)vk);
        MarkSyntheticInput();
    }

    public static void KeyDown(VirtualKey vk)
    {
        SendBatch(MakeVkKey((ushort)vk, false));
        MarkSyntheticInput();
    }

    public static void KeyUp(VirtualKey vk)
    {
        SendBatch(MakeVkKey((ushort)vk, true));
        MarkSyntheticInput();
    }

    // Hotkey helper: e.g. Press(VK_CONTROL), PressAndRelease('C'), Release(VK_CONTROL)
    public static void HotKey(VirtualKey modifier, VirtualKey key)
    {
        KeyDown(modifier);
        try
        {
            Thread.Sleep(InputSettleDelayMs);
            SendKey(key);
            Thread.Sleep(InputSettleDelayMs);
        }
        finally
        {
            KeyUp(modifier);
        }
        MarkSyntheticInput();
    }

    private static void SendBatch(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInput rejected (sent {sent} of {inputs.Length}; another process may hold the foreground).");
    }
}
