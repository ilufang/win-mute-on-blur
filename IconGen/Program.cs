using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IconGen;

/// <summary>
/// Writes the app icon as a multi-size .ico, plus each size as a PNG next to it (app-16.png, ...).
/// Usage: IconGen [output.ico]
/// </summary>
static class Program
{
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    static int Main(string[] args)
    {
        var path = Path.GetFullPath(args.Length > 0 ? args[0] : "app.ico");
        var images = Sizes.Select(size => (Size: size, Data: RenderPng(size))).ToList();

        var pngBase = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        // foreach (var (size, data) in images) File.WriteAllBytes($"{pngBase}-{size}.png", data);
        WriteIco(path, images);

        Console.WriteLine($"Wrote {path} and {pngBase}-*.png ({string.Join(", ", Sizes)} px)");
        return 0;
    }

    static byte[] RenderPng(int size)
    {
        using var bitmap = Render(size);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// <summary>A white "muted speaker" glyph on a blue disc.</summary>
    static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(Color.FromArgb(0x1F, 0x6F, 0xC5));
        g.FillEllipse(background, 0, 0, size - 1, size - 1);
        using var font = new Font("Segoe MDL2 Assets", size * 0.55f, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("", font, Brushes.White, new RectangleF(size*0.03f, size*0.03f, size, size), format);
        return bitmap;
    }

    /// <summary>An ICONDIR header and one entry per size, each image stored as PNG (supported since Vista).</summary>
    static void WriteIco(string path, List<(int Size, byte[] Data)> images)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); // reserved
        w.Write((short)1); // type: icon
        w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width, 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0); // palette colors
            w.Write((byte)0); // reserved
            w.Write((short)1); // color planes
            w.Write((short)32); // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }
}
