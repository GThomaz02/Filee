// HarfBuzz shaping of one run in one font, for tests that draw pages with Filee's page writer (PdfPageWriterTests,
// the render quality checks). The layout engine's own shaper is a later step.

using System.Runtime.InteropServices;
using Filee.Engines.Rendering;
using HarfBuzzSharp;
using SkiaSharp;
using Buffer = HarfBuzzSharp.Buffer;

namespace Filee.Engines.Tests;

internal sealed class TestShaper : IDisposable
{
    private readonly Dictionary<PageFont, (Blob Blob, Face Face, HarfBuzzSharp.Font Font)> _fonts = new(ReferenceEqualityComparer.Instance);

    public float Width(PageFont font, float size, string text) => Shape(font, size, text, 0, 0).Width;

    /// <summary>Shapes <paramref name="text"/> with the pen starting at x on baseline y (page coordinates).</summary>
    public (GlyphRun Run, float Width) Shape(PageFont font, float size, string text, float x, float y, SKColor? color = null)
    {
        var (hb, upem) = For(font);
        using var buffer = new Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        hb.Shape(buffer);
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var scale = size / upem;
        var glyphs = new ushort[infos.Length];
        var points = new SKPoint[infos.Length];
        var clusters = new int[infos.Length];
        var pen = 0f;
        for (var i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            clusters[i] = (int)infos[i].Cluster;
            points[i] = new SKPoint(x + pen + positions[i].XOffset * scale, y - positions[i].YOffset * scale);
            pen += positions[i].XAdvance * scale;
        }
        return (new GlyphRun(font, size, glyphs, points, text, clusters) { Color = color ?? SKColors.Black }, pen);
    }

    private (HarfBuzzSharp.Font Font, int Upem) For(PageFont font)
    {
        if (!_fonts.TryGetValue(font, out var entry))
        {
            // A native copy the blob owns: HarfBuzz reads the font data for as long as the face lives.
            var memory = Marshal.AllocHGlobal(font.Data.Length);
            Marshal.Copy(font.Data, 0, memory, font.Data.Length);
            var blob = new Blob(memory, font.Data.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(memory));
            var face = new Face(blob, font.FaceIndex);
            var hb = new HarfBuzzSharp.Font(face);
            hb.SetScale(face.UnitsPerEm, face.UnitsPerEm);
            hb.SetFunctionsOpenType();
            _fonts[font] = entry = (blob, face, hb);
        }
        return (entry.Font, entry.Face.UnitsPerEm);
    }

    public void Dispose()
    {
        foreach (var (blob, face, font) in _fonts.Values)
        {
            font.Dispose();
            face.Dispose();
            blob.Dispose();
        }
    }
}
