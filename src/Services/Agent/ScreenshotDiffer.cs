// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ScreenshotDiffer.cs
//
// Coarse visual delta between two JPEGs without paying the cost of a full
// pixel decode. Two strategies:
//   1. byte-stride sample: sampled N bytes equally spaced through each
//      JPEG. Identical byte-stride samples → probability that the same
//      pixels-at-the-same-position produced identical compressed bits.
//      False-positive rate is low enough for "did anything change at all"
//      detection; flags very small UI shifts (toast, hover, cursor blink).
//   2. full decode (Windows.Graphics.Imaging): only when callers ask for
//      higher fidelity, e.g. to bracket a click region.
//
// Produces a change-ratio in [0..1] and a list of coarse "hot regions"
// represented by grid cells of 64x64 logical pixels.

using Vantage.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Vantage.Services.Agent;

public sealed record ScreenshotDiff(
    double TotalChangeRatio,
    int ChangedPixelCount,
    int TotalSamples,
    double LargestHeatRegionSize,
    string HotRegionSummary,
    TimeSpan DecodeTime)
{
    public bool IsSignificant(double threshold = 0.01) =>
        TotalChangeRatio >= threshold || LargestHeatRegionSize >= 0.05;
}

public static class ScreenshotDiffer
{
    private const int ByteSampleStride = 64;        // byte stride for fast sample
    private const int PerByteDiffThreshold = 6;      // 6/255 ≈ 2.5% luma shift counts as changed
    private const int GridCellSize = 64;            // 64-px cells for heat-region clustering

    /// <summary>
    /// Fast byte-stride coarse diff. Both buffers should be the same JPEG
    /// of the same logical dimensions (typical case: same capture params).
    /// </summary>
    public static ScreenshotDiff Diff(byte[] jpegBefore, byte[] jpegAfter)
    {
        if (jpegBefore is null || jpegAfter is null ||
            jpegBefore.Length == 0 || jpegAfter.Length == 0)
        {
            return new ScreenshotDiff(1.0, 0, 0, 0, "(empty input)", TimeSpan.Zero);
        }

        // Same length is the optimistic case (capture params are fixed by
        // the worker). When they differ, fall back to length-based diff —
        // we know the screen changed at least as much as the byte stream.
        if (jpegBefore.Length != jpegAfter.Length)
        {
            return new ScreenshotDiff(
                1.0, 0, 0, 1.0,
                $"(size changed: {jpegBefore.Length} -> {jpegAfter.Length})",
                TimeSpan.Zero);
        }

        // Walk the byte stream, skipping JPEG header segments (they contain
        // quantization tables which can vary between encodes of the same
        // image). Sample-aligned reads make the marker-byte noise cancel out.
        const int skipBytes = 1024;
        int start = Math.Min(skipBytes, jpegBefore.Length - 1);
        int stride = Math.Max(1, (jpegBefore.Length - start) / 1024); // ~1024 samples
        int total = 0;
        int diffs = 0;
        for (int i = start; i < jpegBefore.Length; i += stride)
        {
            total++;
            int b = jpegBefore[i];
            int a = jpegAfter[i];
            if (Math.Abs(b - a) > PerByteDiffThreshold) diffs++;
        }

        double ratio = total > 0 ? (double)diffs / total : 0.0;
        return new ScreenshotDiff(
            ratio, diffs, total,
            ratio, // single cluster (no spatial info in byte-stride path)
            $"(byte-stride diff: {diffs}/{total} samples diverged)",
            TimeSpan.Zero);
    }

    /// <summary>
    /// Higher-fidelity diff using BitmapDecoder + grayscale conversion.
    /// Heavier (~30-100 ms per compare) but provides spatial hot regions
    /// used to localize "the click was here, did the click region change?"
    /// </summary>
    public static async Task<ScreenshotDiff> DiffRegionAsync(
        byte[] jpegBefore,
        byte[] jpegAfter,
        int logicalWidth,
        int logicalHeight,
        int focusX = -1,
        int focusY = -1,
        int focusRadiusPx = 96,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (grayBefore, w1, h1) = await DecodeGrayscaleAsync(jpegBefore, ct);
        var (grayAfter,  w2, h2) = await DecodeGrayscaleAsync(jpegAfter, ct);
        sw.Stop();

        var w = Math.Min(w1, w2);
        var h = Math.Min(h1, h2);
        if (w <= 0 || h <= 0 || grayBefore.Length != grayAfter.Length)
        {
            return new ScreenshotDiff(1.0, 0, 0, 1.0,
                "(decode mismatch)", sw.Elapsed);
        }

        int totalSamples = 0;
        int changed = 0;
        int cellW = Math.Max(8, w / GridCellSize);
        int cellH = cellW;
        var cells = new int[(GridCellSize + 1) * (GridCellSize + 1)];

        // If a focus region was given, only count cells in/near it
        bool useFocus = focusX >= 0 && focusY >= 0;
        int focusLeft = Math.Max(0, focusX - focusRadiusPx);
        int focusTop = Math.Max(0, focusY - focusRadiusPx);
        int focusRight = Math.Min(w, focusX + focusRadiusPx);
        int focusBottom = Math.Min(h, focusY + focusRadiusPx);

        int stride = w;
        for (int y = 0; y < h; y += 4) // subsample every 4 px vertically
        {
            int row = y * stride;
            for (int x = 0; x < w; x += 4) // every 4 px horizontally
            {
                if (useFocus && (x < focusLeft || x > focusRight || y < focusTop || y > focusBottom))
                    continue;
                int idx = row + x;
                if (idx >= grayBefore.Length) continue;
                byte bv = grayBefore[idx];
                byte av = grayAfter[idx];
                totalSamples++;
                if (Math.Abs(bv - av) > 10)
                {
                    changed++;
                    int cx = Math.Min(GridCellSize, x / cellW);
                    int cy = Math.Min(GridCellSize, y / cellH);
                    cells[cy * (GridCellSize + 1) + cx]++;
                }
            }
        }

        double ratio = totalSamples > 0 ? (double)changed / totalSamples : 0.0;
        var maxCell = 0;
        var sb = new System.Text.StringBuilder(64);
        for (int cy = 0; cy <= GridCellSize; cy++)
        {
            for (int cx = 0; cx <= GridCellSize; cx++)
            {
                int v = cells[cy * (GridCellSize + 1) + cx];
                if (v > maxCell) maxCell = v;
                if (v > 8) sb.Append('(').Append(cx * cellW).Append(',').Append(cy * cellH).Append("); ");
            }
        }
        double largestRegion = totalSamples > 0 ? (double)maxCell / totalSamples : 0;
        return new ScreenshotDiff(
            ratio, changed, totalSamples,
            largestRegion,
            sb.Length == 0 ? "no hot region" : sb.ToString(0, Math.Min(96, sb.Length)),
            sw.Elapsed);
    }

    private static async Task<(byte[] data, int w, int h)> DecodeGrayscaleAsync(byte[] jpeg, CancellationToken ct)
    {
        // Write the JPEG to a temp file so the decoder can pick it up
        // (BitmapDecoder needs a stream source; RandomAccessStreamReference
        // doesn't accept a raw byte[] directly).
        var tmp = Path.Combine(Path.GetTempPath(), $"vantage_diff_{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(tmp, jpeg, ct);
            var file = await StorageFile.GetFileFromPathAsync(tmp);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int w = (int)decoder.PixelWidth;
            int h = (int)decoder.PixelHeight;
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();
            return (pixels, w, h);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
