// The Filee renderer's PDF output (Rendering/PdfPageWriter.cs): Skia's PDF backend with fonts subset by Filee. The
// acceptance page of the "PDF backend and font subsetting" spike is built here in code: one A4 page with Korean, Latin,
// Arabic and Devanagari paragraphs shaped by HarfBuzz, a link, outline entries and some non-text drawing. It needs
// installed fonts (Malgun Gothic, Segoe UI, Nirmala UI on Windows) and is skipped where they are missing, like the other
// font-dependent tests. Set FILEE_RENDER_OUT to a folder to keep the PDFs for a look in other viewers.

using System.Runtime.InteropServices;
using System.Text;
using Filee.Engines.Rendering;
using HarfBuzzSharp;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using Buffer = HarfBuzzSharp.Buffer;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

#pragma warning disable CA1416

namespace Filee.Engines.Tests;

public sealed class PdfPageWriterTests : IDisposable
{
    private const float A4Width = 595.28f, A4Height = 841.89f, Margin = 50;

    private const string Korean =
        "Filee는 문서를 PDF로 바꿀 때 글꼴 파일 전체를 넣지 않고 실제로 쓴 글자만 골라 담습니다. 맑은 고딕 한 줄 때문에 " +
        "십삼 메가바이트짜리 파일이 생기는 일을 막고, 글자는 그대로 검색하고 복사할 수 있게 남깁니다. 한글 문서의 문단과 표, " +
        "머리말과 꼬리말이 모두 같은 방식으로 그려지며 화면에서 보는 모양과 인쇄한 모양이 같아야 합니다. " +
        "다람쥐 헌 쳇바퀴에 타고파. 키스의 고유조건은 입술끼리 만나야 하고 특별한 기술은 필요치 않다. " +
        "닭 잡아서 치킨파티 함. 콩고물과 우유가 들어간 빙수는 차갑고 달콤하다.";

    private const string Latin =
        "Filee writes searchable PDF files: every font is embedded as a subset that holds only the glyphs the page uses, " +
        "so a page of text stays small and its words can be found, selected and copied. The quick brown fox jumps over " +
        "the lazy dog. Sphinx of black quartz, judge my vow — 0123456789 (€, ©, “quotes”, office, " +
        "finally, waffle).";

    private const string Arabic =
        "يكتب فيلي ملفات قابلة للبحث، وكل خط فيها مضمّن كمجموعة جزئية تحتوي فقط على الحروف المستخدمة في الصفحة. " +
        "هذا نص عربي للتجربة مع التشكيل: بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ.";

    private const string Devanagari =
        "फ़ाइली खोजने योग्य पीडीएफ़ फ़ाइलें लिखता है: हर फ़ॉन्ट का केवल वह हिस्सा जोड़ा जाता है जिसके अक्षर पृष्ठ पर " +
        "इस्तेमाल हुए हैं। हिन्दी में संयुक्ताक्षर जैसे क्ष, त्र, ज्ञ और श्र भी सही दिखने चाहिए।";

    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── The acceptance page ─────────────────────────

    public static TheoryData<string> KoreanFonts() => new()
    {
        "malgun.ttf", // TrueType, 13 MB
        "NotoSansCJKkr-Regular.otf", // CID-keyed CFF (Noto Sans CJK)
        "NotoSansCJK-Regular.ttc",
        "NotoSansKR-VF.ttf", // variable TrueType (Windows 11 ships it)
    };

    [Theory]
    [MemberData(nameof(KoreanFonts))]
    public void Mixed_script_a4_page_is_small_searchable_and_looks_right(string koreanFont)
    {
        var korean = InstalledFont(koreanFont);
        var latin = InstalledFont("segoeui.ttf");
        var devanagari = InstalledFont("Nirmala.ttc", "Nirmala.ttf");
        Assert.SkipUnless(korean is not null && latin is not null && devanagari is not null,
            $"needs {koreanFont}, Segoe UI and Nirmala UI installed");
        using var koreanFace = PageFont.FromFile(korean);
        using var latinFace = PageFont.FromFile(latin);
        using var devanagariFace = PageFont.FromFile(devanagari);
        var (page, text, outline) = MixedPage(koreanFace, latinFace, latinFace, devanagariFace);

        var pdf = Write([page], new PdfDocumentInfo { Title = "Mixed scripts", Outline = outline });
        Keep(pdf, $"mixed-{Path.GetFileNameWithoutExtension(koreanFont)}.pdf");

        // Under 200 KB with every font embedded and subset.
        Assert.True(pdf.Length < 200_000, $"{pdf.Length:N0} bytes");
        var fonts = Fonts(pdf);
        Assert.Equal(3, fonts.Count);
        foreach (var font in fonts)
        {
            Assert.Equal("/Type0", font.Subtype);
            Assert.Equal("/FontFile2", font.File);
            Assert.Matches("^/[A-Z]{6}\\+", font.BaseFont);
        }
        Assert.True(fonts.Sum(f => f.Length) < new FileInfo(korean!).Length / 10);

        // The exact source text, Arabic in logical order, clusters whole.
        Assert.Equal(text, ReadBack(pdf));

        // PDFium draws what Skia draws (rendered at 3× and averaged down, so rasterizer anti-aliasing differences fade).
        const int Dpi = 100;
        using var pdfium = Conversion.ToImage(pdf, 0, null, new RenderOptions(Dpi: 3 * Dpi, WithAnnotations: false, BackgroundColor: SKColors.White));
        using var skia = Raster(page, 3 * Dpi);
        var (ssim, off) = Compare(Downsample(pdfium, 3), Downsample(skia, 3));
        Assert.True(ssim > 0.9 && off < 0.01, $"SSIM {ssim:F4}, {off:P2} of the pixels differ");
    }

    [Fact]
    public void Page_size_links_and_outline_are_exact()
    {
        var latin = InstalledFont("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf", "Arial.ttf");
        Assert.SkipUnless(latin is not null, "no Latin font installed");
        using var face = PageFont.FromFile(latin);
        var page = new PageContent(A4Width, A4Height);
        page.DrawGlyphs(_shaper.Shape(face, 20, "Chapter one", Margin, 100).Run);
        page.LinkToUrl(new SKRect(Margin, 80, 200, 105), "https://github.com/KnifeLemon/Filee");
        page.Destination(new SKPoint(0, 90), "chapter-1");
        var second = new PageContent(400, 300);
        second.DrawGlyphs(_shaper.Shape(face, 12, "Back to chapter one", 20, 40).Run);
        second.LinkToDestination(new SKRect(20, 28, 160, 44), "chapter-1");
        OutlineEntry[] outline = [new("Chapter one", 0, 80, 0), new("Section", 0, 300, 1), new("Second page", 1, 20, 0)];

        var pdf = Write([page, second], new PdfDocumentInfo { Title = "Links", Outline = outline });

        using (var document = PdfPigDocument.Open(pdf))
        {
            var first = document.GetPage(1);
            Assert.Equal(A4Width, first.Width, 0.01);
            Assert.Equal(A4Height, first.Height, 0.01);
            Assert.Equal(400, document.GetPage(2).Width, 0.01);
            // The text stays where it was drawn, measured from the top edge.
            var letter = first.Letters[0];
            Assert.Equal(100, first.Height - letter.StartBaseLine.Y, 0.01); // PdfPig measures from the media box's corner
            Assert.Equal("Chapter one", string.Concat(first.Letters.Select(l => l.Value)));
        }

        using var pdfSharp = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var annotations = pdfSharp.Pages[0].Elements.GetArray("/Annots")!;
        Assert.Contains(Enumerable.Range(0, annotations.Elements.Count), i =>
            annotations.Elements.GetDictionary(i)?.Elements.GetDictionary("/A")?.Elements.GetString("/URI") == "https://github.com/KnifeLemon/Filee");
        Assert.NotNull(pdfSharp.Pages[1].Elements.GetArray("/Annots"));
        var titles = pdfSharp.Outlines.Select(o => o.Title).ToList();
        Assert.Equal(["Chapter one", "Second page"], titles);
        Assert.Equal("Section", Assert.Single(pdfSharp.Outlines[0].Outlines).Title);
    }

    [Fact]
    public void Cff_font_is_embedded_as_a_truetype_subset()
    {
        // FileeTest-CID.otf: a CID-keyed CFF subset of Noto Sans KR (see FontTests), so this runs everywhere.
        using var face = PageFont.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-CID.otf"));
        var page = new PageContent(300, 100);
        var (run, _) = _shaper.Shape(face, 24, "Filee 한글 변환", 10, 50);
        page.DrawGlyphs(run);

        var pdf = Write([page]);

        var font = Assert.Single(Fonts(pdf));
        Assert.Equal("/Type0", font.Subtype);
        Assert.Equal("/FontFile2", font.File);
        Assert.Equal("Filee 한글 변환", ReadBack(pdf));
    }

    [Fact]
    public void Right_to_left_runs_are_written_in_logical_order()
    {
        using var face = PageFont.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf"));
        // A run as a right-to-left shaper reports it: visual order, clusters descending.
        var run = new GlyphRun(face, 10, [3, 2, 1], [new(30, 10), new(20, 10), new(10, 10)], "abc", [2, 1, 0]);

        var (glyphs, positions, clusters) = PageContent.LogicalOrder(run);

        Assert.Equal([1, 2, 3], glyphs);
        Assert.Equal([new SKPoint(10, 10), new SKPoint(20, 10), new SKPoint(30, 10)], positions);
        Assert.Equal([0, 1, 2], clusters);
        // Clusters out of order either way become one cluster for the whole run.
        Assert.Equal([0, 0, 0], PageContent.LogicalOrder(run with { Clusters = [1, 2, 0] }).Clusters);
    }

    [Fact]
    public void Clusters_become_utf8_offsets()
    {
        var (utf8, offsets) = PageContent.Utf8Clusters("aé한😀b", [0, 1, 2, 3, 5]);

        Assert.Equal(Encoding.UTF8.GetBytes("aé한😀b"), utf8);
        Assert.Equal([0u, 1u, 3u, 6u, 10u], offsets);
    }

    // ───────────────────────── Page building ─────────────────────────

    /// <summary>Headings, paragraphs broken at spaces by shaped width, a link and a filled box.</summary>
    private (PageContent Page, string Text, List<OutlineEntry> Outline) MixedPage(PageFont korean, PageFont latin, PageFont arabic, PageFont devanagari)
    {
        var page = new PageContent(A4Width, A4Height);
        var text = new StringBuilder();
        var outline = new List<OutlineEntry>();
        var y = Margin + 20f;
        var accent = new SKColor(0x1F, 0x3A, 0x68);

        void Heading(string title, PageFont font, bool rightToLeft = false)
        {
            var x = rightToLeft ? A4Width - Margin - _shaper.Width(font, 16, title) : Margin;
            page.DrawGlyphs(_shaper.Shape(font, 16, title, x, y, accent).Run);
            text.Append(title);
            outline.Add(new OutlineEntry(title, 0, y - 16, 0));
            var ruleY = y + 5;
            page.Draw(canvas =>
            {
                using var paint = new SKPaint { Color = accent, StrokeWidth = 0.75f, IsAntialias = true };
                canvas.DrawLine(Margin, ruleY, A4Width - Margin, ruleY, paint);
            });
            y += 28;
        }

        void Paragraph(string body, PageFont font, float size, bool rightToLeft = false)
        {
            foreach (var line in Lines(font, size, body, A4Width - 2 * Margin))
            {
                var x = rightToLeft ? A4Width - Margin - _shaper.Width(font, size, line) : Margin;
                page.DrawGlyphs(_shaper.Shape(font, size, line, x, y).Run);
                text.Append(line);
                y += size * 1.6f;
            }
            y += 14;
        }

        Heading("1. 한국어", korean);
        Paragraph(Korean, korean, 11);
        Heading("2. English", latin);
        Paragraph(Latin, latin, 11);
        page.LinkToUrl(new SKRect(Margin, y - 30, Margin + 120, y - 16), "https://github.com/KnifeLemon/Filee");
        Heading("العربية", arabic, rightToLeft: true);
        Paragraph(Arabic, arabic, 13, rightToLeft: true);
        Heading("4. हिन्दी", devanagari);
        Paragraph(Devanagari, devanagari, 12);
        var boxTop = y;
        page.Draw(canvas =>
        {
            using var fill = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF7) };
            canvas.DrawRoundRect(new SKRect(Margin, boxTop, A4Width - Margin, boxTop + 40), 6, 6, fill);
        });
        return (page, text.ToString(), outline);
    }

    private List<string> Lines(PageFont font, float size, string body, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && _shaper.Width(font, size, candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    /// <summary>HarfBuzz shaping of one run in one font (the layout engine's shaper is a later step).</summary>
    private sealed class Shaper : IDisposable
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

    // ───────────────────────── Checks ─────────────────────────

    private static byte[] Write(IReadOnlyList<PageContent> pages, PdfDocumentInfo? info = null)
    {
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, pages, info, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static void Keep(byte[] pdf, string name)
    {
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), pdf);
        }
    }

    /// <summary>A font file from the system's or the user's font folders, by file name; null when none is there.</summary>
    private static string? InstalledFont(params string[] names)
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts"),
        };
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            foreach (var name in names)
            {
                var match = Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
                    .FirstOrDefault();
                if (match is not null)
                    return match;
            }
        }
        return null;
    }

    private sealed record FontInfo(string BaseFont, string Subtype, string? File, int Length);

    /// <summary>The fonts of every page: Type 0 fonts are described by their descendant.</summary>
    private static List<FontInfo> Fonts(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var result = new List<FontInfo>();
        foreach (var page in document.Pages)
        {
            if (page.Resources.Elements.GetDictionary("/Font") is not { } fonts)
                continue;
            foreach (var key in fonts.Elements.Keys)
            {
                var font = fonts.Elements.GetDictionary(key)!;
                var subtype = font.Elements.GetName("/Subtype");
                var described = subtype == "/Type0" ? font.Elements.GetArray("/DescendantFonts")!.Elements.GetDictionary(0)! : font;
                var descriptor = described.Elements.GetDictionary("/FontDescriptor");
                var file = new[] { "/FontFile", "/FontFile2", "/FontFile3" }.FirstOrDefault(k => descriptor?.Elements.GetDictionary(k) is not null);
                result.Add(new FontInfo(font.Elements.GetName("/BaseFont"), subtype, file, file is null ? 0 : descriptor!.Elements.GetDictionary(file)!.Stream.Length));
            }
        }
        return result;
    }

    /// <summary>The text in content-stream order, the glyphs of each /ActualText span replaced by its text.</summary>
    private static string ReadBack(byte[] pdf)
    {
        using var document = PdfPigDocument.Open(pdf);
        var result = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var spans = new Dictionary<Letter, MarkedContentElement>(ReferenceEqualityComparer.Instance);
            void Walk(IEnumerable<MarkedContentElement> elements)
            {
                foreach (var element in elements)
                {
                    if (element.ActualText is null)
                    {
                        Walk(element.Children);
                        continue;
                    }
                    foreach (var letter in All(element))
                        spans.TryAdd(letter, element);
                }
            }
            Walk(page.GetMarkedContents());
            var done = new HashSet<MarkedContentElement>(ReferenceEqualityComparer.Instance);
            foreach (var letter in page.Letters)
            {
                if (!spans.TryGetValue(letter, out var span))
                    result.Append(letter.Value);
                else if (done.Add(span))
                    result.Append(span.ActualText);
            }
        }
        return result.ToString();

        static IEnumerable<Letter> All(MarkedContentElement element) => element.Letters.Concat(element.Children.SelectMany(All));
    }

    private static SKBitmap Raster(PageContent page, int dpi)
    {
        var scale = dpi / 72f;
        var bitmap = new SKBitmap((int)Math.Round(page.Width * scale), (int)Math.Round(page.Height * scale));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        page.Draw(canvas);
        return bitmap;
    }

    /// <summary>Grey levels averaged over <paramref name="factor"/>² blocks.</summary>
    private static (int Width, int Height, double[] Grey) Downsample(SKBitmap bitmap, int factor)
    {
        using var grey = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(grey))
            canvas.DrawBitmap(bitmap, 0, 0);
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
        return (width, height, result);
    }

    /// <summary>
    /// Mean SSIM over 8×8 windows (step 4) that have ink in either picture, and the share of pixels whose grey levels
    /// differ by more than 64.
    /// </summary>
    private static (double Ssim, double Off) Compare((int Width, int Height, double[] Grey) a, (int Width, int Height, double[] Grey) b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        double At((int Width, int Height, double[] Grey) p, int x, int y) => p.Grey[y * p.Width + x];
        const double C1 = 6.5025, C2 = 58.5225;
        double sum = 0;
        var windows = 0;
        for (var y = 0; y + 8 <= h; y += 4)
        {
            for (var x = 0; x + 8 <= w; x += 4)
            {
                double ma = 0, mb = 0;
                for (var j = 0; j < 8; j++)
                    for (var i = 0; i < 8; i++)
                    {
                        ma += At(a, x + i, y + j);
                        mb += At(b, x + i, y + j);
                    }
                ma /= 64;
                mb /= 64;
                double va = 0, vb = 0, cov = 0;
                for (var j = 0; j < 8; j++)
                    for (var i = 0; i < 8; i++)
                    {
                        var da = At(a, x + i, y + j) - ma;
                        var db = At(b, x + i, y + j) - mb;
                        va += da * da;
                        vb += db * db;
                        cov += da * db;
                    }
                if (va + vb < 1e-9 && ma > 250 && mb > 250)
                    continue;
                va /= 63;
                vb /= 63;
                cov /= 63;
                sum += (2 * ma * mb + C1) * (2 * cov + C2) / ((ma * ma + mb * mb + C1) * (va + vb + C2));
                windows++;
            }
        }
        var off = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (Math.Abs(At(a, x, y) - At(b, x, y)) > 64)
                    off++;
        return (windows == 0 ? 1 : sum / windows, (double)off / (w * h));
    }
}
