// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ScreenOcr.cs
//
// Reads a window's text from its pixels with the OCR engine built into
// Windows, for windows that expose little or nothing through UI Automation:
// pictures, canvases, games, remote desktops and some chat apps. A model with
// no screenshot could not see them at all. It runs locally and needs no
// download.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Vantage.Services.Agent;

internal static class ScreenOcr
{
    private const int ExtendedFrameBounds = 9;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    private static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        }
        catch
        {
            return null;
        }
    });

    /// <summary>The window's text as OCR reads it, one line per line, or an empty string.</summary>
    public static async Task<string> ReadWindowAsync(IntPtr window, TimeSpan budget)
    {
        if (window == IntPtr.Zero || Engine.Value is not { } engine) return string.Empty;
        try
        {
            return await ReadCoreAsync(window, engine).WaitAsync(budget);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadCoreAsync(IntPtr window, OcrEngine engine)
    {
        if (CaptureWindow(window) is not { } shot) return string.Empty;
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            shot.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    // What the window shows on screen, which is what OCR should read. The
    // window is in front whenever this runs, so nothing covers it.
    private static (byte[] Pixels, int Width, int Height)? CaptureWindow(IntPtr window)
    {
        if (DwmGetWindowAttribute(window, ExtendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(window, out rect))
            return null;
        var max = (int)OcrEngine.MaxImageDimension;
        var width = Math.Min(rect.Right - rect.Left, max);
        var height = Math.Min(rect.Bottom - rect.Top, max);
        if (width <= 0 || height <= 0) return null;

        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, SrcCopy | CaptureBlt)) return null;
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            return (pixels, width, height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, uint rop);
}
