using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace InfoDive.Services;

/// <summary>
/// Converts SVG bytes into PNG bytes so the rest of the app (which assumes BitmapImage)
/// can consume them without further changes. Small SVGs are scaled up so thumbnails stay
/// legible; oversized SVGs are clamped so the app's 8192px image warning doesn't always fire.
/// </summary>
public static class SvgRasterizer
{
    private const double MinLongestSide = 2048;
    private const double MaxLongestSide = 8192;

    public static bool IsSvg(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".svg", System.StringComparison.OrdinalIgnoreCase);

    public static byte[] Rasterize(byte[] svgBytes)
    {
        var settings = new WpfDrawingSettings
        {
            IncludeRuntime = false,
            TextAsGeometry = false,
        };
        var reader = new FileSvgReader(settings);

        DrawingGroup? drawing;
        using (var ms = new MemoryStream(svgBytes))
        {
            drawing = reader.Read(ms);
        }
        if (drawing == null)
            throw new System.InvalidOperationException("SVGの解析に失敗しました。");

        var bounds = drawing.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new System.InvalidOperationException("SVGのサイズが不正です。");

        var longest = System.Math.Max(bounds.Width, bounds.Height);
        var scale = longest < MinLongestSide ? MinLongestSide / longest
                  : longest > MaxLongestSide ? MaxLongestSide / longest
                  : 1.0;

        var w = System.Math.Max(1, (int)System.Math.Round(bounds.Width * scale));
        var h = System.Math.Max(1, (int)System.Math.Round(bounds.Height * scale));

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
            group.Children.Add(new ScaleTransform(scale, scale));
            ctx.PushTransform(group);
            ctx.DrawDrawing(drawing);
            ctx.Pop();
        }
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var outMs = new MemoryStream();
        encoder.Save(outMs);
        return outMs.ToArray();
    }

    /// <summary>
    /// Returns the stored filename for a rasterized SVG: foo.svg → foo.png. Keeps the
    /// original base name so users still recognise it in the image list.
    /// </summary>
    public static string ToPngFileName(string svgFileName)
        => Path.GetFileNameWithoutExtension(svgFileName) + ".png";
}
