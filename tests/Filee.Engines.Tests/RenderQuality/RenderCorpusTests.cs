// The render quality harness on a private corpus outside the repository: documents next to the PDFs 한글 printed of
// them (doc.hwpx + doc.pdf, or doc.hwp + doc.pdf), in FILEE_RENDER_CORPUS. Each renderer (rhwp, and Filee's layout
// engine once registered) renders every document, which is compared with 한글's PDF and read back; the results go to
// an HTML report (FILEE_RENDER_REPORT, else a file in the temp folder) with the page that differs most of each
// document side by side. It reports and never fails on the numbers: the corpus is private and differs from PC to PC.
// build/render-compare.ps1 runs it.

using System.Globalization;
using System.Net;
using System.Text;

namespace Filee.Engines.Tests.RenderQuality;

[Trait("Category", "RenderCorpus")]
public class RenderCorpusTests
{
    private sealed record Row(string Document, string Renderer, DiffResult? Diff, IReadOnlyList<CheckResult> Checks, TimeSpan Time,
        string? Error, (int Page, byte[] Reference, byte[] Rendered)? Worst);

    [Fact]
    public async Task Corpus_is_compared_with_the_pdfs_hangul_printed()
    {
        var corpus = Environment.GetEnvironmentVariable("FILEE_RENDER_CORPUS");
        Assert.SkipWhen(string.IsNullOrEmpty(corpus), "FILEE_RENDER_CORPUS is not set (see build/render-compare.ps1)");
        Assert.True(Directory.Exists(corpus), $"{corpus} does not exist");
        var pairs = Pairs(corpus).ToList();
        Assert.True(pairs.Count > 0, $"No document with a PDF of the same name in {corpus}");
        var renderers = new[] { DocumentRenderers.Reference, DocumentRenderers.Candidate }.OfType<DocumentRenderer>().ToList();
        Assert.True(renderers.Count > 0, "No renderer: rhwp not found and no layout engine registered");

        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(Path.GetTempPath(), "filee-render-compare", Guid.NewGuid().ToString("N")[..8]);
        var rows = new List<Row>();
        try
        {
            using var gate = new SemaphoreSlim(4);
            var jobs = pairs.SelectMany(pair => renderers.Where(r => r.Formats.Contains(Format(pair.Document))).Select(async renderer =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await CompareAsync(Path.GetRelativePath(corpus, pair.Document), pair.Document, pair.Pdf, renderer, Path.Combine(work, Guid.NewGuid().ToString("N")[..8]), ct);
                }
                finally
                {
                    gate.Release();
                }
            }));
            rows.AddRange(await Task.WhenAll(jobs));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var report = Environment.GetEnvironmentVariable("FILEE_RENDER_REPORT") is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), $"filee-render-compare-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        await File.WriteAllTextAsync(report, Html(corpus, rows), ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"Report: {report}");
        foreach (var row in rows)
            TestContext.Current.TestOutputHelper?.WriteLine($"{row.Document} [{row.Renderer}] {(row.Error ?? row.Diff?.ToString())}");
    }

    /// <summary>Documents with a PDF of the same name next to them.</summary>
    private static IEnumerable<(string Document, string Pdf)> Pairs(string corpus)
    {
        foreach (var file in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Format(file) is not ("hwpx" or "hwp"))
                continue;
            var pdf = Path.ChangeExtension(file, ".pdf");
            if (File.Exists(pdf))
                yield return (file, pdf);
        }
    }

    private static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static async Task<Row> CompareAsync(string name, string document, string printed, DocumentRenderer renderer, string work, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        try
        {
            var reference = await File.ReadAllBytesAsync(printed, ct);
            var started = DateTime.UtcNow;
            var pdf = await renderer.Render(new RenderInput(document, work), ct);
            var time = DateTime.UtcNow - started;
            var referencePages = GreyImage.Pages(reference, RenderDiff.Dpi);
            var renderedPages = GreyImage.Pages(pdf, RenderDiff.Dpi);
            var diff = RenderDiff.Compare(reference, pdf, referencePages, renderedPages);
            var checks = new List<CheckResult>
            {
                PdfChecks.TextRoundTrip(pdf, PdfLines.Text(reference)),
                PdfChecks.OverlappingGlyphs(pdf),
                PdfChecks.GlyphsOutsidePage(pdf),
                PdfChecks.FontsEmbeddedAndSubset(pdf),
            };
            // The page that differs most, both pictures for the report.
            (int, byte[], byte[])? worst = null;
            if (diff.PageSsim.Count > 0)
            {
                var page = diff.PageSsim.Select((s, i) => (s, i)).MinBy(p => p.s).i;
                worst = (page, referencePages[page].ToPng(), renderedPages[page].ToPng());
            }
            return new Row(name, renderer.Name, diff, checks, time, null, worst);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Row(name, renderer.Name, null, [], TimeSpan.Zero, ex.Message, null);
        }
    }

    private static string Html(string corpus, IReadOnlyList<Row> rows)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        static string N(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Filee render compare</title><style>")
            .Append("body{font:14px system-ui,sans-serif;margin:24px;color:#222}table{border-collapse:collapse}")
            .Append("th,td{border:1px solid #ccc;padding:4px 8px;text-align:right}td:first-child,td:nth-child(2){text-align:left}")
            .Append(".bad{background:#fde2e1}.pages{display:flex;gap:12px;margin:8px 0 24px}.pages img{width:420px;border:1px solid #999}")
            .Append("</style></head><body>")
            .Append(CultureInfo.InvariantCulture, $"<h1>Render compare</h1><p>{E(corpus)}: {rows.Select(r => r.Document).Distinct().Count()} documents, ")
            .Append(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm}. Reference: the PDF 한글 printed. Pages drawn by PDFium at {RenderDiff.Dpi} dpi.</p>")
            .Append("<table><tr><th>Document</th><th>Renderer</th><th>Pages (한글 / renderer)</th><th>Lines</th><th>On baseline</th>")
            .Append("<th>SSIM min</th><th>SSIM mean</th><th>Text</th><th>Overlaps</th><th>Outside page</th><th>Fonts not subset</th><th>Time (s)</th></tr>");
        foreach (var row in rows)
        {
            if (row.Diff is not { } diff)
            {
                html.Append(CultureInfo.InvariantCulture, $"<tr class=\"bad\"><td>{E(row.Document)}</td><td>{E(row.Renderer)}</td><td colspan=\"10\">{E(row.Error ?? "")}</td></tr>");
                continue;
            }
            string Check(string name) => row.Checks.FirstOrDefault(c => c.Name == name) is { } c
                ? $"<td{(c.Ok ? "" : " class=\"bad\"")} title=\"{E(c.Detail)}\">{N(c.Value)}</td>"
                : "<td></td>";
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(row.Document)}</td><td>{E(row.Renderer)}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td{(diff.SamePageCount ? "" : " class=\"bad\"")}>{diff.ReferencePages} / {diff.CandidatePages}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td>{N(diff.LineAgreement, "0.0%")}</td><td>{N(diff.BaselineAgreement, "0.0%")}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td>{N(diff.MinSsim, "0.000")}</td><td>{N(diff.MeanSsim, "0.000")}</td>")
                .Append(Check(PdfChecks.Text)).Append(Check(PdfChecks.Overlaps)).Append(Check(PdfChecks.OutsidePage)).Append(Check(PdfChecks.Fonts))
                .Append(CultureInfo.InvariantCulture, $"<td>{N(row.Time.TotalSeconds, "0.0")}</td></tr>");
        }
        html.Append("</table><h2>Pages that differ most</h2>");
        foreach (var row in rows.Where(r => r.Worst is not null))
        {
            var (page, reference, rendered) = row.Worst!.Value;
            html.Append(CultureInfo.InvariantCulture, $"<h3>{E(row.Document)}, page {page + 1}: 한글 and {E(row.Renderer)} (SSIM {N(row.Diff!.PageSsim[page], "0.000")})</h3>")
                .Append("<div class=\"pages\">")
                .Append(CultureInfo.InvariantCulture, $"<img alt=\"한글\" src=\"data:image/png;base64,{Convert.ToBase64String(reference)}\">")
                .Append(CultureInfo.InvariantCulture, $"<img alt=\"{E(row.Renderer)}\" src=\"data:image/png;base64,{Convert.ToBase64String(rendered)}\">")
                .Append("</div>");
        }
        return html.Append("</body></html>").ToString();
    }
}
