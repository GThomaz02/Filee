// Runs the feature matrix through a renderer and measures the PDFs: the read-back checks for every rendering, the
// differential compare where there is something to compare with. Renderings run four at a time (rhwp is a process per
// document). FILEE_RENDER_OUT keeps the documents, the PDFs and their pages as PNG in a folder for a look.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>A feature's document rendered by one renderer.</summary>
/// <param name="Key">The feature name, with ".hwp" for the binary HWP made from its HWPX.</param>
/// <param name="Input">The rendered document.</param>
internal sealed record Rendering(RenderFeature Feature, string Key, string Input, byte[] Pdf, TimeSpan Time);

internal static class RenderHarness
{
    private const int Parallel = 4;

    /// <summary>
    /// Writes each feature's document into <paramref name="root"/> and renders it (and, with
    /// <paramref name="withHwp"/>, the binary HWP of the features that ask for one).
    /// </summary>
    public static async Task<List<Rendering>> RenderAsync(DocumentRenderer renderer, IEnumerable<RenderFeature> features, string root,
        bool withHwp, CancellationToken cancellationToken, Func<string, string>? prepare = null)
    {
        using var gate = new SemaphoreSlim(Parallel);
        var jobs = new List<Task<Rendering>>();
        foreach (var feature in features)
        {
            var folder = Path.Combine(root, renderer.Name, feature.Name);
            var hwpx = feature.Write(folder);
            if (prepare is not null)
                hwpx = prepare(hwpx);
            jobs.Add(RunAsync(feature, feature.Name, hwpx));
            if (withHwp && feature.Hwp && renderer.Formats.Contains("hwp"))
                jobs.Add(RunAsync(feature, feature.Name + ".hwp", null, hwpx));
        }
        return [.. await Task.WhenAll(jobs)];

        async Task<Rendering> RunAsync(RenderFeature feature, string key, string? path, string? convertFrom = null)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                path ??= await DocumentRenderers.ConvertToHwpAsync(convertFrom!, cancellationToken);
                var work = Path.Combine(Path.GetDirectoryName(path)!, "work-" + Path.GetExtension(path).TrimStart('.'));
                Directory.CreateDirectory(work);
                var watch = Stopwatch.StartNew();
                var pdf = await renderer.Render(new RenderInput(path, work), cancellationToken);
                var rendering = new Rendering(feature, key, path, pdf, watch.Elapsed);
                Keep(renderer.Name, rendering);
                return rendering;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>The read-back checks of one rendering.</summary>
    public static List<CheckResult> ReadBack(Rendering rendering)
    {
        var pdf = rendering.Pdf;
        var results = new List<CheckResult>
        {
            PdfChecks.TextRoundTrip(pdf, rendering.Feature.Text),
            PdfChecks.OverlappingGlyphs(pdf),
            PdfChecks.GlyphsOutsidePage(pdf),
        };
        if (Margins(rendering.Input) is { } margins)
            results.Add(PdfChecks.GlyphsOutsideMargins(pdf, margins));
        if (rendering.Feature.Pages is { } pages)
            results.Add(PdfChecks.PageCount(pdf, pages));
        if (rendering.Feature.Pitch is { } pitch)
            results.Add(PdfChecks.BaselinePitch(pdf, pitch));
        results.Add(PdfChecks.FontsEmbeddedAndSubset(pdf));
        return results;
    }

    /// <summary>The differential compare as measurements: same page count, lines, lines on their baselines, lowest SSIM.</summary>
    public static List<CheckResult> Differences(DiffResult diff) =>
    [
        new(PdfChecks.Pages, diff.CandidatePages, diff.SamePageCount, diff.SamePageCount ? "" : $"reference has {diff.ReferencePages}"),
        new(RenderThresholds.Lines, diff.LineAgreement, diff.LineAgreement >= 1, $"{diff.ReferenceLines}/{diff.CandidateLines} lines{(diff.Exact ? "" : ", approximate")}"),
        new(RenderThresholds.Placed, diff.BaselineAgreement, diff.BaselineAgreement >= 1, ""),
        new(RenderThresholds.MinSsim, diff.MinSsim, diff.MinSsim >= 0.999, string.Create(CultureInfo.InvariantCulture, $"mean {diff.MeanSsim:0.####}")),
    ];

    /// <summary>The body margins of the document's first section, when its file is an HWPX that sets them.</summary>
    public static PageMargins? Margins(string path)
    {
        if (!path.EndsWith(".hwpx", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".hwpx"); // a binary HWP: the HWPX it was made from
        if (!File.Exists(path))
            return null;
        var media = Path.Combine(Path.GetTempPath(), "filee-render-media", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var document = HwpxReader.ReadForLayout(path, media);
            // Several sections with different paper: the checks would need page-by-page margins.
            if (document.Sections.Select(s => s.Page).Distinct().Count() != 1 || document.Sections[0].Page is not { } page)
                return null;
            return new PageMargins(page.Left / 100.0, page.Top / 100.0, page.Right / 100.0, page.Bottom / 100.0);
        }
        finally
        {
            try { Directory.Delete(media, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Keep(string renderer, Rendering rendering)
    {
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is not { Length: > 0 } folder)
            return;
        var target = Path.Combine(folder, renderer);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, rendering.Key + ".pdf"), rendering.Pdf);
        File.Copy(rendering.Input, Path.Combine(target, Path.GetFileName(rendering.Input)), overwrite: true);
        // The pages as PDFium draws them for the compare.
        var pages = GreyImage.Pages(rendering.Pdf, RenderDiff.Dpi);
        for (var i = 0; i < pages.Count; i++)
            File.WriteAllBytes(Path.Combine(target, $"{rendering.Key}-{i + 1}.png"), pages[i].ToPng());
    }

    // ───────────────────────── Report ─────────────────────────

    /// <summary>
    /// A table of the measurements, one row per feature: each value with its verdict (ok = ideal, ≤ = within the
    /// threshold, ↑ = better than the threshold, allowed, FAIL), then the regressions and improvements spelled out.
    /// </summary>
    public static string Report(IReadOnlyList<Measurement> measurements, RenderThresholds thresholds, bool markdown)
    {
        var checks = measurements.Select(m => m.Result.Name).Distinct().ToList();
        var builder = new StringBuilder();
        var rows = measurements.GroupBy(m => (m.Renderer, m.Feature)).ToList();
        string Cell(Measurement m)
        {
            var value = m.Result.Value.ToString("0.####", CultureInfo.InvariantCulture);
            return thresholds.Judge(m) switch
            {
                Verdict.Ideal => value,
                Verdict.WithinThreshold => "≤ " + value,
                Verdict.Improved => "↑ " + value,
                Verdict.Allowed => value + " (allowed)",
                _ => "FAIL " + value,
            };
        }
        string[] header = ["renderer", "feature", .. checks];
        var table = rows.Select(r => (string[])[r.Key.Renderer, r.Key.Feature, .. checks.Select(c => r.FirstOrDefault(m => m.Result.Name == c) is { } m ? Cell(m) : "")]).ToList();
        if (markdown)
        {
            builder.AppendLine("| " + string.Join(" | ", header) + " |");
            builder.AppendLine("|" + string.Concat(header.Select(_ => "---|")));
            foreach (var row in table)
                builder.AppendLine("| " + string.Join(" | ", row) + " |");
        }
        else
        {
            var widths = header.Select((h, i) => Math.Max(h.Length, table.Count == 0 ? 0 : table.Max(r => r[i].Length))).ToArray();
            builder.AppendLine(string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))));
            foreach (var row in table)
                builder.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
        }
        builder.AppendLine();
        foreach (var m in measurements)
        {
            var verdict = thresholds.Judge(m);
            if (verdict == Verdict.Regression)
                builder.AppendLine($"- REGRESSION {m.Renderer} {m.Feature}: {m.Result} (threshold {thresholds.Threshold(m.Renderer, m.Feature, m.Result.Name)?.ToString(CultureInfo.InvariantCulture) ?? "ideal"}; measured {RenderThresholds.Suggest(m.Result)})");
            else if (verdict == Verdict.Improved)
                builder.AppendLine($"- improved {m.Renderer} {m.Feature}: {m.Result} (raise the threshold to {RenderThresholds.Suggest(m.Result)})");
            else if (verdict == Verdict.Allowed && m.Result.Ok)
                builder.AppendLine($"- allowed {m.Renderer} {m.Feature} {m.Result.Name} now meets the ideal: the allow-list entry can go");
            else if (verdict == Verdict.Ideal && thresholds.Threshold(m.Renderer, m.Feature, m.Result.Name) is not null)
                builder.AppendLine($"- improved {m.Renderer} {m.Feature}: {m.Result} meets the ideal, the threshold can go");
        }
        return builder.ToString();
    }
}
