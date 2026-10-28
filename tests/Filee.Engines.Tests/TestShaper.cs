// Shaping of one run in one font, for tests that draw pages with Filee's page writer (PdfPageWriterTests, the render
// quality checks): the layout engine's shaper (Typesetting/Shaper.cs), placed with the pen at a point of the page.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Tests;

internal sealed class TestShaper : IDisposable
{
    private readonly Typesetting.Shaper _shaper = new();

    public float Width(PageFont font, float size, string text) => _shaper.Shape(font, text).Width(size);

    /// <summary>Shapes <paramref name="text"/> with the pen starting at x on baseline y (page coordinates).</summary>
    public (GlyphRun Run, float Width) Shape(PageFont font, float size, string text, float x, float y, SKColor? color = null)
    {
        var run = _shaper.Shape(font, text);
        return (run.ToGlyphRun(font, size, new SKPoint(x, y), text) with { Color = color ?? SKColors.Black }, run.Width(size));
    }

    public void Dispose() => _shaper.Dispose();
}
