using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class Program
{
    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? Path.GetFullPath(args[0]) : "app.ico";

        // If the user committed their own app.ico, never overwrite it.
        if (File.Exists(outPath))
        {
            Console.WriteLine($"Icon already exists at {outPath}, skipping generation.");
            return;
        }

        string? dir = Path.GetDirectoryName(outPath);

        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        int[] sizes = { 16, 32, 48, 256 };
        var pngs = new List<byte[]>();

        foreach (int size in sizes)
        {
            using Bitmap bmp = DrawIcon(size);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            pngs.Add(png.ToArray());
        }

        File.WriteAllBytes(outPath, BuildIco(pngs, sizes));
        Console.WriteLine($"Generated {outPath}");
    }

    private static Bitmap DrawIcon(int size)
    {
        var bmp = new Bitmap(size, size);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size;

        var bgRect = new RectangleF(0, 0, s, s);

        using (var path = RoundedRect(bgRect, s * 0.18f))
        using (var brush = new LinearGradientBrush(
                   bgRect,
                   Color.FromArgb(255, 41, 128, 185),
                   Color.FromArgb(255, 20, 61, 110),
                   90f))
        {
            g.FillPath(brush, path);
        }

        using var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.10f));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;

        float left = s * 0.26f;

        g.DrawLine(pen, left, s * 0.30f, s * 0.74f, s * 0.30f);
        g.DrawLine(pen, left, s * 0.50f, s * 0.60f, s * 0.50f);
        g.DrawLine(pen, left, s * 0.70f, s * 0.46f, s * 0.70f);

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();

        path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static byte[] BuildIco(List<byte[]> pngs, int[] sizes)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((short)0);
        bw.Write((short)1);
        bw.Write((short)pngs.Count);

        int offset = 6 + 16 * pngs.Count;

        for (int i = 0; i < pngs.Count; i++)
        {
            int size = sizes[i];

            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(pngs[i].Length);
            bw.Write(offset);

            offset += pngs[i].Length;
        }

        foreach (byte[] png in pngs)
            bw.Write(png);

        return ms.ToArray();
    }
}
