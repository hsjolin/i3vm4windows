using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace i3vm4windows.App.Services;

/// <summary>
/// Draws the application icon: a tiling layout in miniature — one large
/// pane beside two stacked ones on a dark, rounded square. Rendered at
/// every size the tray asks for, so it stays crisp at any DPI instead of
/// a scaled-down 32-pixel default icon.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] Sizes = { 16, 32, 48 };

    public static Icon Create()
    {
        using var stream = new MemoryStream();
        WriteIconStream(stream);
        stream.Position = 0;
        return new Icon(stream);
    }

    /// <summary>
    /// Assembles an .ico container with PNG frames for every size, the
    /// format Vista and later shells pick the best frame from.
    /// </summary>
    private static void WriteIconStream(Stream target)
    {
        var frames = Sizes
            .Select(size => (Size: size, Png: EncodePng(DrawTile(size))))
            .ToArray();

        using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);

        var offset = 6 + 16 * frames.Length;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
            writer.Write(png);
    }

    private static byte[] EncodePng(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// The tiling motif: a large pane on the left, two smaller ones
    /// stacked on the right, all laid out in proportion to the canvas so
    /// the small sizes keep their gaps and rounded corners.
    /// </summary>
    private static Bitmap DrawTile(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var border = Math.Max(1, size / 8);
        var gap = size >= 32 ? 2 : 1;
        var inner = new RectangleF(border, border, size - 2 * border, size - 2 * border);

        DrawBackground(graphics, size, inner);
        DrawPane(graphics, inner with
        {
            Width = (inner.Width - gap) * 0.58f,
        }, size / 10, Color.FromArgb(92, 169, 233), Color.FromArgb(53, 114, 184));

        var right = new RectangleF(
            inner.X + (inner.Width - gap) * 0.58f + gap,
            inner.Y,
            inner.Width - (inner.Width - gap) * 0.58f - gap,
            inner.Height);
        DrawPane(graphics, right with
        {
            Height = (inner.Height - gap) / 2,
        }, size / 10, Color.FromArgb(143, 193, 233), Color.FromArgb(124, 174, 220));
        DrawPane(graphics, right with
        {
            Y = right.Y + (inner.Height - gap) / 2 + gap,
            Height = (inner.Height - gap) / 2,
        }, size / 10, Color.FromArgb(92, 147, 206), Color.FromArgb(72, 120, 172));

        return bitmap;
    }

    private static void DrawBackground(Graphics graphics, int size, RectangleF rect)
    {
        using var brush = new LinearGradientBrush(
            rect, Color.FromArgb(43, 51, 69), Color.FromArgb(20, 23, 31),
            LinearGradientMode.Vertical);
        using var path = RoundedRect(rect, size / 6f);
        graphics.FillPath(brush, path);
    }

    private static void DrawPane(
        Graphics graphics, RectangleF rect, int radius, Color top, Color bottom)
    {
        using var brush = new LinearGradientBrush(
            rect, top, bottom, LinearGradientMode.Vertical);
        using var path = RoundedRect(rect, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (radius < 1 || rect.Width < radius * 2 || rect.Height < radius * 2)
        {
            path.AddRectangle(rect);
            return path;
        }

        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
