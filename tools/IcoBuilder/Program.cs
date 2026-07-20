using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// IcoBuilder — assemble a multi-resolution .ico from per-size PNGs, deterministically.
// Layout: 32bpp BGRA BMP DIB (all-zero AND mask) for 16..64; raw PNG for 256 — the
// container every Windows shell version handles. Given identical PNGs the output is
// byte-identical. Zero NuGet packages (in-box PngBitmapDecoder via UseWPF).
//   Usage: IcoBuilder <pngDir> -o <out.ico>   (expects <pngDir>/{16,24,32,48,64,256}.png)

namespace IcoBuilder;

internal static class Program
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 256 };

    private static int Main(string[] args)
    {
        string? dir = null, outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
            else dir ??= args[i];
        }
        if (dir is null || outPath is null)
        {
            Console.Error.WriteLine("usage: IcoBuilder <pngDir> -o <out.ico>");
            return 2;
        }

        var frames = new List<(int size, byte[] data)>();
        foreach (var s in Sizes)
        {
            var p = Path.Combine(dir, s + ".png");
            if (!File.Exists(p)) { Console.Error.WriteLine($"missing {p}"); return 3; }
            var png = File.ReadAllBytes(p);
            frames.Add((s, s == 256 ? png : BuildBmpDib(png, s)));
        }

        using var fs = File.Create(outPath);
        using var w = new BinaryWriter(fs);
        // ICONDIR
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type = icon
        w.Write((ushort)frames.Count);   // image count
        int offset = 6 + frames.Count * 16;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
            w.Write((byte)0);                        // color count (0 = truecolor)
            w.Write((byte)0);                        // reserved
            w.Write((ushort)1);                      // planes
            w.Write((ushort)32);                     // bit count
            w.Write((uint)data.Length);              // bytes in resource
            w.Write((uint)offset);                   // image offset
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);

        Console.WriteLine($"wrote {outPath} — {frames.Count} frames, {fs.Length} bytes");
        return 0;
    }

    // A 32bpp bottom-up BGRA DIB (BITMAPINFOHEADER, biHeight doubled for the all-zero AND mask).
    private static byte[] BuildBmpDib(byte[] png, int size)
    {
        var dec = new PngBitmapDecoder(new MemoryStream(png),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bmp = dec.Frames[0];
        if (bmp.Format != PixelFormats.Bgra32)
            bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w != size || h != size)
            throw new InvalidDataException($"{size}.png is {w}x{h}, expected {size}x{size}");

        int stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);                // top-down BGRA

        int maskRow = ((w + 31) / 32) * 4;            // AND-mask row, padded to 32 bits
        var buf = new byte[40 + stride * h + maskRow * h];
        using var ms = new MemoryStream(buf);
        using var bw = new BinaryWriter(ms);
        // BITMAPINFOHEADER
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((ushort)1); bw.Write((ushort)32);    // planes, bitcount
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        // XOR bitmap, bottom-up
        for (int y = h - 1; y >= 0; y--) bw.Write(px, y * stride, stride);
        // AND mask stays all-zero (alpha carries transparency)
        return buf;
    }
}
