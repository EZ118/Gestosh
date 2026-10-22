using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    static void Main()
    {
        string root = @"E:\Gestosh";
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var bmp = Draw(sizes[i]))
                images[i] = BuildDib(bmp);
        }
        string path = Path.Combine(root, @"assets\Gestosh.ico");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)images[i].Length);
                writer.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (var image in images) writer.Write(image);
        }
        Console.WriteLine("wrote " + path + " (" + new FileInfo(path).Length + " bytes)");
    }

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float inset = Math.Max(0.5f, size * 0.055f);
            float radius = Math.Max(2f, size * 0.22f);
            var background = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
            using (var path = Rounded(background, radius))
            using (var fill = new SolidBrush(Color.FromArgb(255, 15, 23, 42)))
            using (var border = new Pen(Color.FromArgb(255, 51, 65, 85), Math.Max(0.8f, size * 0.022f)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            // Touchpad surface.
            float padX = size * 0.22f;
            float padY = size * 0.60f;
            float padW = size * 0.56f;
            float padH = size * 0.26f;
            var pad = new RectangleF(padX, padY, padW, padH);
            using (var path = Rounded(pad, Math.Max(1.4f, size * 0.055f)))
            using (var pen = new Pen(Color.FromArgb(255, 203, 213, 225), Math.Max(1f, size * 0.035f)))
                g.DrawPath(pen, path);

            // Five fingertips, fanned out above the surface.
            DrawDot(g, size, 0.50f, 0.28f, 0.062f, Color.FromArgb(255, 147, 197, 253));
            DrawDot(g, size, 0.33f, 0.36f, 0.052f, Color.FromArgb(255, 96, 165, 250));
            DrawDot(g, size, 0.67f, 0.36f, 0.052f, Color.FromArgb(255, 96, 165, 250));
            DrawDot(g, size, 0.20f, 0.46f, 0.043f, Color.FromArgb(255, 59, 130, 246));
            DrawDot(g, size, 0.80f, 0.46f, 0.043f, Color.FromArgb(255, 59, 130, 246));
        }
        return bmp;
    }

    static void DrawDot(Graphics g, int size, float cx, float cy, float radius, Color color)
    {
        float r = Math.Max(0.7f, size * radius);
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, size * cx - r, size * cy - r, r * 2, r * 2);
    }

    static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static byte[] BuildDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskRow = ((w + 31) / 32) * 4;
        int maskSize = maskRow * h;
        int imageSize = w * h * 4 + maskSize;
        var data = new byte[40 + imageSize];
        using (var ms = new MemoryStream(data))
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(40);
            writer.Write(w);
            writer.Write(h * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(w * h * 4 + maskSize);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    writer.Write(c.B);
                    writer.Write(c.G);
                    writer.Write(c.R);
                    writer.Write(c.A);
                }
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < maskRow; x++)
                    writer.Write((byte)0);
        }
        return data;
    }
}
