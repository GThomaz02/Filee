// Character → font slot for 한글's seven language slots and Word's four run fonts (Typesetting/FontSlots.cs).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public class FontSlotsTests
{
    [Theory]
    [InlineData('가', nameof(HLanguage.Hangul))]
    [InlineData(0x1100, nameof(HLanguage.Hangul))] // conjoining jamo
    [InlineData('ㄱ', nameof(HLanguage.Hangul))] // compatibility jamo
    [InlineData('A', nameof(HLanguage.Latin))]
    [InlineData('7', nameof(HLanguage.Latin))]
    [InlineData(' ', nameof(HLanguage.Latin))]
    [InlineData('.', nameof(HLanguage.Latin))]
    [InlineData('é', nameof(HLanguage.Latin))]
    [InlineData('×', nameof(HLanguage.Latin))]
    [InlineData('漢', nameof(HLanguage.Hanja))]
    [InlineData('あ', nameof(HLanguage.Japanese))]
    [InlineData('ア', nameof(HLanguage.Japanese))]
    [InlineData('ー', nameof(HLanguage.Japanese))] // Common, but Japanese
    [InlineData('ｱ', nameof(HLanguage.Japanese))]
    [InlineData('“', nameof(HLanguage.Symbol))]
    [InlineData('…', nameof(HLanguage.Symbol))]
    [InlineData('。', nameof(HLanguage.Symbol))]
    [InlineData('→', nameof(HLanguage.Symbol))]
    [InlineData('Ａ', nameof(HLanguage.Symbol))] // fullwidth form
    [InlineData('α', nameof(HLanguage.Other))]
    [InlineData('Ж', nameof(HLanguage.Other))]
    [InlineData('ب', nameof(HLanguage.Other))]
    [InlineData('ก', nameof(HLanguage.Other))]
    [InlineData(0xE000, nameof(HLanguage.User))]
    public void Hwp_language_slots(int codePoint, string expected)
    {
        Assert.Equal(Enum.Parse<HLanguage>(expected), FontSlots.Hwp(codePoint));
    }

    [Theory]
    [InlineData('A', nameof(WordFontHint.Default), nameof(WordFontSlot.Ascii))]
    [InlineData('é', nameof(WordFontHint.Default), nameof(WordFontSlot.HighAnsi))]
    [InlineData('Ж', nameof(WordFontHint.Default), nameof(WordFontSlot.HighAnsi))]
    [InlineData('“', nameof(WordFontHint.Default), nameof(WordFontSlot.HighAnsi))]
    [InlineData('“', nameof(WordFontHint.EastAsia), nameof(WordFontSlot.EastAsia))] // ambiguous width with the East Asian hint
    [InlineData('Ж', nameof(WordFontHint.EastAsia), nameof(WordFontSlot.EastAsia))]
    [InlineData('é', nameof(WordFontHint.EastAsia), nameof(WordFontSlot.EastAsia))]
    [InlineData('A', nameof(WordFontHint.EastAsia), nameof(WordFontSlot.Ascii))]
    [InlineData('가', nameof(WordFontHint.Default), nameof(WordFontSlot.EastAsia))]
    [InlineData('漢', nameof(WordFontHint.Default), nameof(WordFontSlot.EastAsia))]
    [InlineData('。', nameof(WordFontHint.Default), nameof(WordFontSlot.EastAsia))]
    [InlineData('Ａ', nameof(WordFontHint.Default), nameof(WordFontSlot.EastAsia))]
    [InlineData('ب', nameof(WordFontHint.Default), nameof(WordFontSlot.ComplexScript))]
    [InlineData('א', nameof(WordFontHint.Default), nameof(WordFontSlot.ComplexScript))]
    [InlineData(0x0915, nameof(WordFontHint.Default), nameof(WordFontSlot.ComplexScript))]
    [InlineData('ก', nameof(WordFontHint.Default), nameof(WordFontSlot.ComplexScript))]
    [InlineData(0xE000, nameof(WordFontHint.Default), nameof(WordFontSlot.HighAnsi))]
    [InlineData(0xE000, nameof(WordFontHint.EastAsia), nameof(WordFontSlot.EastAsia))]
    public void Word_run_fonts(int codePoint, string hint, string expected)
    {
        Assert.Equal(Enum.Parse<WordFontSlot>(expected), FontSlots.Word(codePoint, Enum.Parse<WordFontHint>(hint)));
    }

    [Fact]
    public void Complex_script_runs_use_the_cs_font_for_everything()
    {
        Assert.Equal(WordFontSlot.ComplexScript, FontSlots.Word('A', complexScript: true));
        Assert.Equal("Arial", new HWordFonts("Calibri", "Calibri", "맑은 고딕", "Arial").Font(WordFontSlot.ComplexScript));
        Assert.Equal(WordFontHint.EastAsia, FontSlots.Hint("eastAsia"));
    }

    [Fact]
    public void Runs_split_by_slot_and_keep_clusters_whole()
    {
        // "e" + combining acute stays Latin; the jamo sequence is one cluster in the Hangul slot.
        const string text = "Cafe\u0301 한글 \u1100\u1161\u11A8 “漢字”";

        var runs = FontSlots.HwpRuns(text, 0, text.Length);

        Assert.Equal(
            [("Cafe\u0301 ", HLanguage.Latin), ("한글", HLanguage.Hangul), (" ", HLanguage.Latin), ("\u1100\u1161\u11A8", HLanguage.Hangul),
                (" ", HLanguage.Latin), ("“", HLanguage.Symbol), ("漢字", HLanguage.Hanja), ("”", HLanguage.Symbol)],
            runs.Select(r => (text.Substring(r.Start, r.Length), r.Slot)));
    }

    [Fact]
    public void Word_runs_cover_a_part_of_the_text()
    {
        const string text = "xx Hello 世界 مرحبا xx";

        var runs = FontSlots.WordRuns(text, 3, text.Length - 6);

        Assert.Equal(
            [("Hello ", WordFontSlot.Ascii), ("世界", WordFontSlot.EastAsia), (" ", WordFontSlot.Ascii), ("مرحبا", WordFontSlot.ComplexScript)],
            runs.Select(r => (text.Substring(r.Start, r.Length), r.Slot)));
    }
}
