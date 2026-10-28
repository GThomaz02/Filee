// Which of a run's fonts draws a character. 한글 gives each character format seven language slots (HLanguageSlots:
// 한글, 영문, 한자, 일어, 외국어, 기호, 사용자) with a font, 장평, 자간, relative size and offset each; Word gives a run
// four fonts (HWordFonts: ascii, hAnsi, eastAsia, cs). The layout splits text into runs by slot before it resolves
// fonts and shapes.
//
// A grapheme cluster takes the slot of its first character, so combining marks, variation selectors and joiners stay
// with their base (one font per cluster; the shaper needs them together).
//
// 한글: by script (Hangul, Han, kana, Latin), with the Common characters of the ASCII / Latin-1 / spacing modifier range
// in 영문 like 한글 does, other punctuation and symbols (quotes, dashes, CJK punctuation, fullwidth forms, arrows,
// box drawing, emoji) in 기호, private use in 사용자, and every other script (Greek, Cyrillic, Arabic, Thai, ...) in
// 외국어. Word: ASCII in ascii, complex scripts (right-to-left and Brahmic / Southeast Asian scripts) in cs, CJK
// scripts and blocks in eastAsia and the rest in hAnsi. With w:hint="eastAsia", ambiguous-width characters (curly
// quotes, dashes, ellipsis, Greek and Cyrillic) and private use go to eastAsia, as Word does for East Asian text;
// Word's further distinction by the run's East Asian language (Latin-1 letters in Chinese text) is not made.

using System.Globalization;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Typesetting;

/// <summary>Word's run fonts (w:rFonts).</summary>
internal enum WordFontSlot
{
    /// <summary>w:ascii: U+0000–U+007F.</summary>
    Ascii,

    /// <summary>w:hAnsi: other Latin, Greek, Cyrillic, symbols, ...</summary>
    HighAnsi,

    /// <summary>w:eastAsia: CJK text.</summary>
    EastAsia,

    /// <summary>w:cs: complex-script text (Arabic, Hebrew, Indic, Thai, ...).</summary>
    ComplexScript,
}

/// <summary>Word's w:hint: which font ambiguous characters take.</summary>
internal enum WordFontHint
{
    Default,
    EastAsia,
    ComplexScript,
}

/// <summary>A piece of text whose characters all fall into one font slot.</summary>
/// <param name="Start">UTF-16 index of the first character.</param>
/// <param name="Length">Length in UTF-16 code units.</param>
internal readonly record struct SlotRun<TSlot>(int Start, int Length, TSlot Slot)
{
    public int End => Start + Length;
}

/// <summary>Character → font slot for 한글's language slots and Word's run fonts.</summary>
internal static class FontSlots
{
    /// <summary>The 한글 language slot of a character.</summary>
    public static HLanguage Hwp(int codePoint)
    {
        if (IsPrivateUse(codePoint))
            return HLanguage.User;
        switch (UnicodeData.GetScript(codePoint))
        {
            case UnicodeScript.Hangul:
                return HLanguage.Hangul;
            case UnicodeScript.Han:
                return HLanguage.Hanja;
            case UnicodeScript.Hiragana or UnicodeScript.Katakana or UnicodeScript.KatakanaOrHiragana:
                return HLanguage.Japanese;
            case UnicodeScript.Latin:
                return IsFullwidthForm(codePoint) ? HLanguage.Symbol : HLanguage.Latin;
            case UnicodeScript.Common or UnicodeScript.Inherited:
                if (codePoint < 0x0300)
                    return HLanguage.Latin;
                // The prolonged sound mark and the kana punctuation (ー ・ ゛) are Common but Japanese.
                if (codePoint is >= 0x3099 and <= 0x30FF or >= 0xFF70 and <= 0xFF9F)
                    return HLanguage.Japanese;
                return HLanguage.Symbol;
            default:
                return HLanguage.Other;
        }
    }

    /// <summary>The Word run font a character is drawn with.</summary>
    /// <param name="complexScript">The run is complex-script text (w:cs or w:rtl): every character takes cs.</param>
    public static WordFontSlot Word(int codePoint, WordFontHint hint = WordFontHint.Default, bool complexScript = false)
    {
        if (complexScript)
            return WordFontSlot.ComplexScript;
        if (codePoint < 0x80)
            return WordFontSlot.Ascii;
        if (IsPrivateUse(codePoint))
            return hint == WordFontHint.EastAsia ? WordFontSlot.EastAsia : WordFontSlot.HighAnsi;
        var script = UnicodeData.GetScript(codePoint);
        if (IsComplexScript(script))
            return WordFontSlot.ComplexScript;
        if (IsEastAsian(script, codePoint))
            return WordFontSlot.EastAsia;
        if (hint == WordFontHint.EastAsia && UnicodeData.GetEastAsianWidth(codePoint) == EastAsianWidth.Ambiguous)
            return WordFontSlot.EastAsia;
        return WordFontSlot.HighAnsi;
    }

    /// <summary>The font a Word run gives a slot (null when the run leaves it open).</summary>
    public static string? Font(this HWordFonts fonts, WordFontSlot slot) => slot switch
    {
        WordFontSlot.Ascii => fonts.Ascii,
        WordFontSlot.HighAnsi => fonts.HighAnsi,
        WordFontSlot.EastAsia => fonts.EastAsia,
        _ => fonts.ComplexScript,
    };

    /// <summary>Word's w:hint value as a <see cref="WordFontHint"/>.</summary>
    public static WordFontHint Hint(string? hint) => hint switch
    {
        "eastAsia" => WordFontHint.EastAsia,
        "cs" => WordFontHint.ComplexScript,
        _ => WordFontHint.Default,
    };

    /// <summary>Splits <c>text[start..start+length]</c> into runs of one 한글 language slot.</summary>
    public static List<SlotRun<HLanguage>> HwpRuns(string text, int start, int length) => Runs(text, start, length, Hwp);

    /// <summary>Splits <c>text[start..start+length]</c> into runs of one Word run font.</summary>
    public static List<SlotRun<WordFontSlot>> WordRuns(string text, int start, int length, WordFontHint hint = WordFontHint.Default,
        bool complexScript = false) =>
        Runs(text, start, length, c => Word(c, hint, complexScript));

    /// <summary>
    /// Splits text into runs of one slot: each grapheme cluster takes the slot of its first character, and neighbouring
    /// clusters of the same slot form one run.
    /// </summary>
    public static List<SlotRun<TSlot>> Runs<TSlot>(string text, int start, int length, Func<int, TSlot> classify)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);
        var runs = new List<SlotRun<TSlot>>();
        var comparer = EqualityComparer<TSlot>.Default;
        var end = start + length;
        var position = start;
        while (position < end)
        {
            var cluster = Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(position, end - position)));
            var slot = classify(UnicodeData.CodePointAt(text, position));
            if (runs.Count > 0 && comparer.Equals(runs[^1].Slot, slot))
                runs[^1] = runs[^1] with { Length = runs[^1].Length + cluster };
            else
                runs.Add(new SlotRun<TSlot>(position, cluster, slot));
            position += cluster;
        }
        return runs;
    }

    private static bool IsPrivateUse(int codePoint) =>
        codePoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0x10FFFD;

    /// <summary>The fullwidth ASCII variants (Ａ, ０, ！): CJK typographic forms, drawn by the East Asian font.</summary>
    private static bool IsFullwidthForm(int codePoint) => codePoint is >= 0xFF01 and <= 0xFF60;

    /// <summary>Scripts Word draws with the cs font: right-to-left, Brahmic and Southeast Asian scripts.</summary>
    private static bool IsComplexScript(UnicodeScript script) => script is UnicodeScript.Arabic or UnicodeScript.Hebrew
        or UnicodeScript.Syriac or UnicodeScript.Thaana or UnicodeScript.Nko or UnicodeScript.Samaritan or UnicodeScript.Mandaic
        or UnicodeScript.Devanagari or UnicodeScript.Bengali or UnicodeScript.Gurmukhi or UnicodeScript.Gujarati
        or UnicodeScript.Oriya or UnicodeScript.Tamil or UnicodeScript.Telugu or UnicodeScript.Kannada or UnicodeScript.Malayalam
        or UnicodeScript.Sinhala or UnicodeScript.Thai or UnicodeScript.Lao or UnicodeScript.Tibetan or UnicodeScript.Myanmar
        or UnicodeScript.Khmer;

    /// <summary>CJK scripts and the CJK punctuation, symbol and compatibility blocks.</summary>
    private static bool IsEastAsian(UnicodeScript script, int codePoint) =>
        script is UnicodeScript.Han or UnicodeScript.Hangul or UnicodeScript.Hiragana or UnicodeScript.Katakana
            or UnicodeScript.KatakanaOrHiragana or UnicodeScript.Bopomofo or UnicodeScript.Yi
        || codePoint is >= 0x2E80 and <= 0x2FFF // CJK radicals, Kangxi radicals, ideographic description
            or >= 0x3000 and <= 0x303F // CJK symbols and punctuation
            or >= 0x3190 and <= 0x33FF // kanbun, CJK strokes, enclosed CJK, CJK compatibility
            or >= 0xFE10 and <= 0xFE1F // vertical forms
            or >= 0xFE30 and <= 0xFE4F // CJK compatibility forms
            or >= 0xFF00 and <= 0xFFEF; // halfwidth and fullwidth forms
}
