// The layout engine's HarfBuzz shaper (Typesetting/Shaper.cs) on Arabic, Devanagari, Gujarati, Thai and Hangul jamo
// text written here. Each script is checked three ways:
// - against HarfBuzz itself: the same text shaped the plain way (one buffer, properties guessed by HarfBuzz) gives the
//   same glyphs, clusters, advances and offsets;
// - against recorded HarfBuzz output (hb-shape's text format) for the pinned Noto fonts build/fetch-fonts.ps1 puts in
//   src/Filee.App/Assets/Fonts (CI fetches them; without them those tests skip; there is no pinned Gujarati font);
// - by what the script needs, independent of the font: Arabic joining forms and lam-alef, Indic vowel signs before
//   their consonant and conjuncts, Thai SARA AM split, Hangul jamo composed into the syllable, no dotted circles.
// Fonts come from the repository's Noto fonts or the installed ones (Segoe UI, Nirmala UI, Leelawadee UI, Malgun
// Gothic, ...); a script is skipped when no font for it is found.

using System.Runtime.InteropServices;
using System.Text;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using HarfBuzzSharp;
using SkiaSharp;
using Buffer = HarfBuzzSharp.Buffer;

namespace Filee.Engines.Tests;

public sealed class ShaperTests : IDisposable
{
    private const string Arabic = "السلام عليكم، لا بأس. بِسْمِ اللَّهِ";
    private const string Devanagari = "क्षत्रिय कि हिन्दी श्री";
    private const string Gujarati = "ક્ષત્રિય કિ ગુજરાતી શ્રી";
    private const string Thai = "ที่นี่ น้ำ กำลัง";
    private const string Jamo = "\u1100\u1161\u11A8 \u1112\u1161\u11AB\u1100\u1173\u11AF";

    /// <summary>Recorded HarfBuzz 8.3 output (hb-shape's text format, font units) for the pinned Noto fonts.</summary>
    private static readonly Dictionary<string, string> Recorded = new()
    {
        ["NotoSansArabic-Regular.ttf"] =
            "[427=34@111,0+0|85=34+486|371=31@-35,456+0|368=31@-27,256+0|70=31+300|72=30+260|8=29+238|3=28+260|" +
            "427=26@139,0+0|77=26+562|379=24@301,-126+0|36=24+850|427=22@-35,-191+0|317=22@31,-3+0|19=22+269|" +
            "3=21+260|172=20+268|34=19+1209|298=18@-17,126+0|13=18+289|317=17@31,-3+0|19=17+269|3=16+260|" +
            "10=15+363|73=14+219|3=13+260|176=12+328|77=11+562|60=10+508|319=9@39,-5+0|18=9+373|70=8+300|" +
            "49=7+503|3=6+260|74=5+484|11=4+379|71=3+220|36=2+850|72=1+260|8=0+238]",
        ["NotoSansDevanagari-Regular.ttf"] =
            "[90=0+717|545=3+259|304=3+552|81=7+580|3=8+260|545=9+259|56=9+768|3=11+260|544=12+259|88=12+531|" +
            "245=14+309|73=16+531|33=16+259|3=18+260|317=19+708|33=19+259]",
        ["NotoSansThai-Regular.ttf"] =
            "[117=0+609|94=0@-3,0+0|44=0@-3,0+0|71=3+613|94=3+0|44=3+0|111=6+260|71=7+613|59=7+0|49=7@-29,0+0|" +
            "86=7+406|111=10+260|29=11+600|59=11@-2,0+0|86=11+406|33=13+571|45=13@-7,0+0|58=15+537]",
        ["NotoSansKR-Regular.otf"] =
            "[10317=0+920|1=3+224|20904=4+920|10828=7+920]",
    };

    private readonly Shaper _shaper = new();
    private readonly List<PageFont> _fonts = [];

    public void Dispose()
    {
        _shaper.Dispose();
        foreach (var font in _fonts)
            font.Dispose();
    }

    /// <summary>Each script's text with the pinned Noto font and the fonts systems install for it.</summary>
    public static TheoryData<string, string> Fonts() => new()
    {
        { "NotoSansArabic-Regular.ttf", Arabic }, { "segoeui.ttf", Arabic }, { "arial.ttf", Arabic }, { "DejaVuSans.ttf", Arabic },
        { "NotoSansDevanagari-Regular.ttf", Devanagari }, { "Nirmala.ttc", Devanagari }, { "Lohit-Devanagari.ttf", Devanagari },
        { "Nirmala.ttc", Gujarati }, { "shruti.ttf", Gujarati }, { "NotoSansGujarati-Regular.ttf", Gujarati }, { "Lohit-Gujarati.ttf", Gujarati },
        { "NotoSansThai-Regular.ttf", Thai }, { "LeelawUI.ttf", Thai }, { "tahoma.ttf", Thai },
        { "NotoSansKR-Regular.otf", Jamo }, { "malgun.ttf", Jamo }, { "NotoSansCJK-Regular.ttc", Jamo },
    };

    // ───────────────────────── Against HarfBuzz ─────────────────────────

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Shaping_matches_harfbuzz(string file, string text)
    {
        var path = FindFont(file);
        Assert.SkipUnless(path is not null, $"{file} is not installed");
        var font = Load(path);

        var run = _shaper.Shape(font, text);

        Assert.Equal(Reference(font, text), Serialize(run));
    }

    [Theory]
    [InlineData("NotoSansArabic-Regular.ttf", Arabic)]
    [InlineData("NotoSansDevanagari-Regular.ttf", Devanagari)]
    [InlineData("NotoSansThai-Regular.ttf", Thai)]
    [InlineData("NotoSansKR-Regular.otf", Jamo)]
    public void Shaping_matches_recorded_harfbuzz_output(string file, string text)
    {
        var path = RepositoryFont(file);
        Assert.SkipUnless(path is not null, $"{file} is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var font = Load(path);

        var run = _shaper.Shape(font, text);

        Assert.Equal(Recorded[file], Serialize(run));
        Assert.Equal(Recorded[file], Reference(font, text));
    }

    // ───────────────────────── What each script needs ─────────────────────────

    [Fact]
    public void Arabic_joins_right_to_left_with_contextual_forms_and_lam_alef()
    {
        var font = FontFor("Arabic");
        Assert.SkipUnless(font is not null, "no Arabic font found");

        // Some fonts draw the dots of a letter as glyphs of their own (without advance), so letters are compared by the
        // glyph that carries the advance.
        var word = _shaper.Shape(font, "ببب");
        Assert.Equal(TextDirection.RightToLeft, word.Direction);
        Assert.Equal([2, 1, 0], word.Clusters.Distinct()); // visual order: the last letter first
        Assert.Equal(word.Clusters.OrderDescending(), word.Clusters);
        var forms = Enumerable.Range(0, 3).Select(c => Base(word, c)).ToList();
        Assert.Equal(3, forms.Distinct().Count()); // initial, medial and final forms
        Assert.DoesNotContain(Base(_shaper.Shape(font, "ب"), 0), forms); // none is the isolated form

        // Lam before alef is the lam-alef ligature (or a form of its own), not the lam that starts other words.
        Assert.NotEqual(Base(_shaper.Shape(font, "لب"), 0), Base(_shaper.Shape(font, "لا"), 0));

        var marked = _shaper.Shape(font, "بِ"); // a kasra: zero advance, in its letter's cluster
        Assert.All(marked.Clusters, c => Assert.Equal(0, c));
        Assert.Contains(0, marked.XAdvances);
        NoMissingGlyphs(font, _shaper.Shape(font, Arabic));
    }

    [Theory]
    [InlineData("Devanagari", "\u0915\u093F", "\u0915\u094D\u0937", '\u0915', '\u093F')] // कि, क्ष
    [InlineData("Gujarati", "\u0A95\u0ABF", "\u0A95\u0ACD\u0AB7", '\u0A95', '\u0ABF')] // કિ, ક્ષ
    public void Indic_vowel_signs_reorder_and_conjuncts_form(string script, string syllable, string conjunct, char consonant, char sign)
    {
        var font = FontFor(script);
        Assert.SkipUnless(font is not null, $"no {script} font found");

        // The i vowel sign is written after its consonant and drawn before it.
        var reordered = _shaper.Shape(font, syllable);
        Assert.Equal(2, reordered.Glyphs.Length);
        Assert.Equal([0, 0], reordered.Clusters);
        Assert.NotEqual(Nominal(font, consonant), reordered.Glyphs[0]);
        Assert.Equal(Nominal(font, consonant), reordered.Glyphs[1]);

        // Consonant + virama + consonant is one conjunct cluster with fewer glyphs than characters.
        var joined = _shaper.Shape(font, conjunct);
        Assert.True(joined.Glyphs.Length < conjunct.Length);
        Assert.All(joined.Clusters, c => Assert.Equal(0, c));
        NoMissingGlyphs(font, _shaper.Shape(font, script == "Devanagari" ? Devanagari : Gujarati));

        // A vowel sign without a consonant gets a dotted circle, as HarfBuzz marks broken clusters.
        Assert.Contains(Nominal(font, '\u25CC'), _shaper.Shape(font, sign.ToString()).Glyphs);
    }

    [Fact]
    public void Thai_sara_am_is_split_and_marks_stay_in_their_cluster()
    {
        var font = FontFor("Thai");
        Assert.SkipUnless(font is not null, "no Thai font found");

        // น้ำ: NO NU, MAI THO, SARA AM. SARA AM becomes NIKHAHIT + SARA AA.
        var run = _shaper.Shape(font, "น้ำ");
        Assert.Contains(Nominal(font, 'า'), run.Glyphs);
        Assert.True(run.Glyphs.Length >= 4);
        Assert.All(run.Clusters, c => Assert.Equal(0, c));

        // ที่: the tone mark sits on the vowel above, without an advance of its own.
        var stacked = _shaper.Shape(font, "ที่");
        Assert.Equal(3, stacked.Glyphs.Length);
        Assert.Equal(0, stacked.XAdvances[2]);
        NoMissingGlyphs(font, _shaper.Shape(font, Thai));
    }

    [Fact]
    public void Hangul_jamo_compose_into_syllables()
    {
        var font = FontFor("Hangul");
        Assert.SkipUnless(font is not null, "no Hangul font found");

        var run = _shaper.Shape(font, Jamo);

        // ᄀ ᅡ ᆨ is 각, ᄒ ᅡ ᆫ 한, ᄀ ᅳ ᆯ 글: one glyph each, the precomposed syllable's.
        Assert.Equal([Nominal(font, '각'), Nominal(font, ' '), Nominal(font, '한'), Nominal(font, '글')], run.Glyphs);
        Assert.Equal([0, 3, 4, 7], run.Clusters);

        // Old Hangul (ᄒ ᆞ ᆫ) has no precomposed syllable: the jamo stay one cluster, drawn by the font's jamo features.
        const string old = "\u1112\u119E\u11AB";
        if (font.Typeface.GetGlyph(0x119E) != 0)
        {
            var archaic = _shaper.Shape(font, old);
            Assert.All(archaic.Clusters, c => Assert.Equal(0, c));
            NoMissingGlyphs(font, archaic);
        }
    }

    // ───────────────────────── Runs, context and cache ─────────────────────────

    [Fact]
    public void A_run_is_shaped_with_the_text_around_it()
    {
        var font = FontFor("Arabic");
        Assert.SkipUnless(font is not null, "no Arabic font found");
        const string word = "بببب";

        // Two runs of one word (a colour change in the middle) join like the whole word.
        var whole = _shaper.Shape(font, word);
        var first = _shaper.Shape(font, word, 0, 2);
        var second = _shaper.Shape(font, word, 2, 2);

        Assert.Equal(whole.Glyphs, second.Glyphs.Concat(first.Glyphs)); // right to left: the second run is drawn first
        Assert.Equal([1, 0], first.Clusters.Distinct());
        Assert.Equal([1, 0], second.Clusters.Distinct()); // counted from the run's start
    }

    [Fact]
    public void Runs_are_cached_by_text_context_and_options()
    {
        using var shaper = new Shaper();
        var font = TestFont();

        var once = shaper.Shape(font, "Filee 한글");
        Assert.Same(once, shaper.Shape(font, "Filee 한글"));
        Assert.Same(once, shaper.Shape(font, "Filee 한글", 0, 8));
        Assert.Equal(1, shaper.Shaped);

        shaper.Shape(font, "x Filee 한글", 2, 8); // other context
        shaper.Shape(font, "Filee 한글", new ShapeOptions(Features: [new FontFeature("kern", 0)]));
        shaper.Shape(font, "Filee 한글", new ShapeOptions(Language: "ko"));
        Assert.Equal(4, shaper.Shaped);
    }

    [Fact]
    public void A_shaped_run_becomes_a_glyph_run_of_the_page_model()
    {
        var font = TestFont();
        var run = _shaper.Shape(font, "Filee 한글");

        var glyphs = run.ToGlyphRun(font, 20, new SKPoint(10, 50), "Filee 한글", scaleX: 0.5f);

        glyphs.Validate();
        Assert.Equal("Filee 한글", glyphs.Text);
        Assert.Equal(new SKPoint(10, 50), glyphs.Positions[0]);
        Assert.Equal(10 + run.Width(20) * 0.5f, glyphs.Positions[^1].X + run.XAdvances[^1] * 20f / run.UnitsPerEm * 0.5f, 3);
        Assert.Equal(0.5f, glyphs.ScaleX);

        var metrics = _shaper.Metrics(font);
        Assert.Equal(run.UnitsPerEm, metrics.UnitsPerEm);
        Assert.True(metrics.Ascender > 0 && metrics.Descender < 0 && metrics.UnderlineThickness > 0);
    }

    // ───────────────────────── Fonts and the reference ─────────────────────────


    /// <summary>The test font every system has (Latin and Korean, see FontTests).</summary>
    private PageFont TestFont() => Load(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf"));

    /// <summary>A font for a script: the repository's pinned Noto font, else an installed one, else null.</summary>
    private PageFont? FontFor(string script)
    {
        string[] names = script switch
        {
            "Arabic" => ["NotoSansArabic-Regular.ttf", "segoeui.ttf", "arial.ttf", "tahoma.ttf", "DejaVuSans.ttf"],
            "Devanagari" => ["NotoSansDevanagari-Regular.ttf", "Nirmala.ttc", "Nirmala.ttf", "mangal.ttf", "Lohit-Devanagari.ttf"],
            "Gujarati" => ["NotoSansGujarati-Regular.ttf", "Nirmala.ttc", "Nirmala.ttf", "shruti.ttf", "Lohit-Gujarati.ttf"],
            "Thai" => ["NotoSansThai-Regular.ttf", "LeelawUI.ttf", "tahoma.ttf"],
            _ => ["NotoSansKR-Regular.otf", "malgun.ttf", "NotoSansCJK-Regular.ttc"],
        };
        foreach (var name in names)
        {
            if (FindFont(name) is { } path)
                return Load(path);
        }
        return null;
    }

    private PageFont Load(string path)
    {
        var font = PageFont.FromFile(path);
        _fonts.Add(font);
        return font;
    }

    /// <summary>A font build/fetch-fonts.ps1 put into src/Filee.App/Assets/Fonts; null when it isn't there.</summary>
    private static string? RepositoryFont(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
            {
                var path = Path.Combine(dir.FullName, "src", "Filee.App", "Assets", "Fonts", name);
                return File.Exists(path) ? path : null;
            }
        }
        return null;
    }

    /// <summary>A font file from the repository's app fonts, or the system's or the user's font folders.</summary>
    private static string? FindFont(string name)
    {
        if (RepositoryFont(name) is { } pinned)
            return pinned;
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts",
        ];
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            var match = Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
                .FirstOrDefault();
            if (match is not null)
                return match;
        }
        return null;
    }

    private static ushort Nominal(PageFont font, int codePoint) => font.Typeface.GetGlyph(codePoint);

    /// <summary>The glyph of a cluster that carries its advance (the letter, not its dots or marks).</summary>
    private static ushort Base(ShapedRun run, int cluster) =>
        Enumerable.Range(0, run.Glyphs.Length).Where(i => run.Clusters[i] == cluster).MaxBy(i => run.XAdvances[i]) is var i ? run.Glyphs[i] : (ushort)0;

    /// <summary>No .notdef and no dotted circle: every character was drawn and every cluster was well formed.</summary>
    private static void NoMissingGlyphs(PageFont font, ShapedRun run)
    {
        Assert.DoesNotContain((ushort)0, run.Glyphs);
        var circle = Nominal(font, '\u25CC');
        if (circle != 0)
            Assert.DoesNotContain(circle, run.Glyphs);
    }

    /// <summary>
    /// The text shaped by HarfBuzz the plain way: one buffer with all the text, segment properties guessed by HarfBuzz,
    /// the font at its units per em; serialized by HarfBuzz in hb-shape's text format.
    /// </summary>
    private static string Reference(PageFont font, string text)
    {
        var memory = Marshal.AllocHGlobal(font.Data.Length);
        Marshal.Copy(font.Data, 0, memory, font.Data.Length);
        using var blob = new Blob(memory, font.Data.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(memory));
        using var face = new Face(blob, font.FaceIndex);
        using var hb = new HarfBuzzSharp.Font(face);
        hb.SetFunctionsOpenType();
        hb.SetScale(face.UnitsPerEm, face.UnitsPerEm);
        using var buffer = new Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        hb.Shape(buffer);
        return buffer.SerializeGlyphs(hb, SerializeFormat.Text, SerializeFlag.NoGlyphNames);
    }

    /// <summary>A shaped run in hb-shape's text format: [glyph=cluster@x,y+advance|...], offsets when not 0.</summary>
    private static string Serialize(ShapedRun run)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < run.Glyphs.Length; i++)
        {
            if (i > 0)
                text.Append('|');
            text.Append(run.Glyphs[i]).Append('=').Append(run.Clusters[i]);
            if (run.XOffsets[i] != 0 || run.YOffsets[i] != 0)
                text.Append('@').Append(run.XOffsets[i]).Append(',').Append(run.YOffsets[i]);
            text.Append('+').Append(run.XAdvances[i]);
            if (run.YAdvances[i] != 0)
                text.Append(',').Append(run.YAdvances[i]);
        }
        return text.Append(']').ToString();
    }
}
