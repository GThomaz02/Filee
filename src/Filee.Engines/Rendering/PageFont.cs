// A font the page model draws glyphs from: the font file's bytes (the PDF writer subsets them) and a Skia typeface
// over the whole font (for shaping, measuring and drawing PNG pages).

using Filee.Engines.Fonts;
using SkiaSharp;

namespace Filee.Engines.Rendering;

/// <summary>One face of a font file, as the layout engine uses it.</summary>
internal sealed class PageFont : IDisposable
{
    private SKTypeface? _typeface;
    private SfntFont? _sfnt;

    private PageFont(byte[] data, int faceIndex, SKTypeface? typeface)
    {
        Data = data;
        FaceIndex = faceIndex;
        _typeface = typeface;
    }

    /// <summary>The font file (TTF, OTF or TTC).</summary>
    public byte[] Data { get; }

    /// <summary>Index of the face in a TTC collection; 0 otherwise.</summary>
    public int FaceIndex { get; }

    /// <summary>A Skia typeface over the whole font.</summary>
    public SKTypeface Typeface => _typeface ??= SKTypeface.FromData(SKData.CreateCopy(Data), FaceIndex)
        ?? throw new InvalidDataException("Skia can't read the font.");

    /// <summary>The font's tables.</summary>
    public SfntFont Sfnt => _sfnt ??= SfntFont.ReadFace(Data, FaceIndex);

    /// <summary>Reads face <paramref name="faceIndex"/> of a font file.</summary>
    public static PageFont FromFile(string path, int faceIndex = 0) => FromData(File.ReadAllBytes(path), faceIndex);

    /// <summary>A font from the bytes of a font file.</summary>
    public static PageFont FromData(byte[] data, int faceIndex = 0)
    {
        if (!SfntFont.IsSfnt(data))
            throw new InvalidDataException("This is not a TrueType or OpenType font.");
        return new PageFont(data, faceIndex, null);
    }

    /// <summary>The font file behind a typeface Skia found (an installed font, a font manager match).</summary>
    public static PageFont FromTypeface(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream(out var index) ?? throw new InvalidDataException($"Skia can't read the font data of {typeface.FamilyName}.");
        var data = new byte[stream.Length];
        if (stream.Read(data, data.Length) != data.Length)
            throw new InvalidDataException($"Skia can't read the font data of {typeface.FamilyName}.");
        if (!SfntFont.IsSfnt(data))
            throw new InvalidDataException($"{typeface.FamilyName} is not a TrueType or OpenType font.");
        return new PageFont(data, index, null);
    }

    public void Dispose() => _typeface?.Dispose();
}
