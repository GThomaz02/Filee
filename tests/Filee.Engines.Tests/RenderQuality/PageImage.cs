// Pages as grey pictures and how alike two of them look: PDFium (PDFtoImage) rasterises the PDF, and the structural
// similarity index (SSIM) compares the pictures window by window. Windows blank in both pictures are left out, so a
// page with little text isn't rated alike just for its white paper.

using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416 // PDFtoImage runs on Windows, Linux and macOS, the platforms the tests run on

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>A grey picture: one level per pixel, 0 black to 255 white.</summary>
internal sealed record GreyImage(int Width, int Height, double[] Pixels)
{
    public double this[int x, int y] => Pixels[y * Width + x];

    /// <summary>Grey levels of <paramref name="bitmap"/> averaged over <paramref name="factor"/>² blocks.</summary>
    public static GreyImage From(SKBitmap bitmap, int factor = 1)
    {
        using var grey = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(grey))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        var pixels = grey.GetPixelSpan();
        var stride = grey.RowBytes;
        int width = bitmap.Width / factor, height = bitmap.Height / factor;
        var result = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var j = 0; j < factor; j++)
                    for (var i = 0; i < factor; i++)
                        sum += pixels[(y * factor + j) * stride + x * factor + i];
                result[y * width + x] = (double)sum / (factor * factor);
            }
        }
        return new GreyImage(width, height, result);
    }

    /// <summary>Every page of <paramref name="pdf"/> drawn by PDFium on white at <paramref name="dpi"/>.</summary>
    public static List<GreyImage> Pages(byte[] pdf, int dpi = 100)
    {
        var pages = new List<GreyImage>();
        foreach (var bitmap in Conversion.ToImages(pdf, null, new RenderOptions(Dpi: dpi, WithAnnotations: false, BackgroundColor: SKColors.White)))
        {
            using (bitmap)
                pages.Add(From(bitmap));
        }
        return pages;
    }

    /// <summary>The picture as a PNG (for reports).</summary>
    public byte[] ToPng()
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Gray8, SKAlphaType.Opaque);
        var span = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                span[y * stride + x] = (byte)Math.Clamp(Math.Round(this[x, y]), 0, 255);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

internal static class Ssim
{
    private const int Window = 8, Step = 4;

    // The usual constants for 8-bit pictures: (0.01 × 255)² and (0.03 × 255)².
    private const double C1 = 6.5025, C2 = 58.5225;

    /// <summary>
    /// Mean SSIM over 8×8 windows (step 4) that have ink in either picture, and the share of pixels whose grey levels
    /// differ by more than 64. Pictures of different sizes are compared over the area they share.
    /// </summary>
    public static (double Ssim, double Off) Compare(GreyImage a, GreyImage b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        double sum = 0;
        var windows = 0;
        for (var y = 0; y + Window <= h; y += Step)
        {
            for (var x = 0; x + Window <= w; x += Step)
            {
                double ma = 0, mb = 0;
                for (var j = 0; j < Window; j++)
                {
                    for (var i = 0; i < Window; i++)
                    {
                        ma += a[x + i, y + j];
                        mb += b[x + i, y + j];
                    }
                }
                ma /= Window * Window;
                mb /= Window * Window;
                double va = 0, vb = 0, cov = 0;
                for (var j = 0; j < Window; j++)
                {
                    for (var i = 0; i < Window; i++)
                    {
                        var da = a[x + i, y + j] - ma;
                        var db = b[x + i, y + j] - mb;
                        va += da * da;
                        vb += db * db;
                        cov += da * db;
                    }
                }
                if (va + vb < 1e-9 && ma > 250 && mb > 250)
                    continue; // paper in both
                va /= Window * Window - 1;
                vb /= Window * Window - 1;
                cov /= Window * Window - 1;
                sum += (2 * ma * mb + C1) * (2 * cov + C2) / ((ma * ma + mb * mb + C1) * (va + vb + C2));
                windows++;
            }
        }
        var off = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (Math.Abs(a[x, y] - b[x, y]) > 64)
                    off++;
        return (windows == 0 ? 1 : sum / windows, w * h == 0 ? 0 : (double)off / (w * h));
    }
}
