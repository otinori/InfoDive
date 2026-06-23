using System;
using System.Collections.Generic;
using System.IO;
using PDFtoImage;
using SkiaSharp;

namespace InfoDive.Services;

/// <summary>
/// Rasterizes each page of a PDF into PNG bytes so each page becomes a separate
/// "image" in the project, reusing the existing add-image pipeline unchanged.
/// </summary>
public static class PdfRasterizer
{
    private const int DefaultDpi = 200;

    public static bool IsPdf(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns one (PNG bytes, filename) entry per PDF page.
    /// Filenames: foo.pdf → foo_p01.png, foo_p02.png, ...
    /// </summary>
    public static List<(byte[] PngBytes, string FileName)> Rasterize(byte[] pdfBytes, string fileName, int dpi = DefaultDpi)
    {
        var pageCount = Conversion.GetPageCount(pdfBytes);
        if (pageCount <= 0)
            throw new InvalidOperationException("PDFのページが取得できません。");

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var pad = Math.Max(2, pageCount.ToString().Length);
        var result = new List<(byte[], string)>(pageCount);

        for (var i = 0; i < pageCount; i++)
        {
            using var bmp = Conversion.ToImage(pdfBytes, page: (Index)i, options: new RenderOptions(Dpi: dpi));
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            var pageNum = (i + 1).ToString().PadLeft(pad, '0');
            result.Add((data.ToArray(), $"{baseName}_p{pageNum}.png"));
        }
        return result;
    }
}
