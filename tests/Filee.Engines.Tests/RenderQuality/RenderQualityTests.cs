// The render quality harness on the feature matrix (RenderFeatures): every document rendered by rhwp, the reference,
// goes through the read-back checks, its binary HWP twin is compared with it, and every measurement is held to
// render-thresholds.json. Once Filee's layout engine registers a renderer (DocumentRenderers.Candidate), its PDFs
// get the same checks and are compared with rhwp's. The metrics are printed per feature (and added to the job
// summary on GitHub Actions).
//
// rhwp's results depend on the fonts installed, so the thresholds are those of the Windows CI runner and the
// matrix runs on Windows only. The self-comparison and the perturbed renderings prove the compare itself: rhwp
// against rhwp must be identical, and a shifted or re-wrapped rendering must be caught.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests.RenderQuality;

[Trait("Category", "Render")]
public class RenderQualityTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static DocumentRenderer Rhwp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "rhwp's thresholds are measured with the fonts of Windows");
        var rhwp = DocumentRenderers.Reference;
        Assert.SkipUnless(rhwp is not null, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        return rhwp;
    }

    [Fact]
    public async Task Feature_matrix_rendered_by_rhwp_holds_its_thresholds()
    {
        var rhwp = Rhwp();
        var thresholds = RenderThresholds.Load();
        var ct = TestContext.Current.CancellationToken;

        var renderings = await RenderHarness.RenderAsync(rhwp, RenderFeatures.Selected, fx.NewFolder(), withHwp: true, ct);

        var measurements = new List<Measurement>();
        foreach (var rendering in renderings)
        {
            measurements.AddRange(RenderHarness.ReadBack(rendering).Select(r => new Measurement(rhwp.Name, rendering.Key, r)));
            // rhwp's HWP against its HWPX: one document, so the two renderings should agree.
            if (rendering.Key.EndsWith(".hwp", StringComparison.Ordinal))
            {
                var fromHwpx = renderings.Single(r => r.Key == rendering.Feature.Name);
                measurements.AddRange(RenderHarness.Differences(RenderDiff.Compare(fromHwpx.Pdf, rendering.Pdf))
                    .Where(r => r.Name != PdfChecks.Pages || rendering.Feature.Pages is null) // else measured against the feature's count
                    .Select(r => new Measurement(rhwp.Name, rendering.Key, r)));
            }
        }

        Publish("rhwp", measurements, thresholds, renderings);
        var regressions = measurements.Where(m => thresholds.Judge(m) == Verdict.Regression).ToList();
        Assert.True(regressions.Count == 0, "Render quality regressions:\n" + string.Join("\n", regressions.Select(m => $"{m.Feature}: {m.Result}")));
    }

    [Fact]
    public async Task Feature_matrix_rendered_by_filee_holds_its_thresholds_against_rhwp()
    {
        var rhwp = Rhwp();
        var filee = DocumentRenderers.Candidate;
        Assert.SkipWhen(filee is null, DocumentRenderers.NoCandidate);
        var thresholds = RenderThresholds.Load();
        var ct = TestContext.Current.CancellationToken;
        var root = fx.NewFolder();

        var features = RenderFeatures.Selected;
        var reference = await RenderHarness.RenderAsync(rhwp, features, root, withHwp: false, ct);
        var candidate = await RenderHarness.RenderAsync(filee!, features, root, withHwp: true, ct);

        var measurements = new List<Measurement>();
        foreach (var rendering in candidate)
        {
            measurements.AddRange(RenderHarness.ReadBack(rendering).Select(r => new Measurement(filee!.Name, rendering.Key, r)));
            var against = reference.Single(r => r.Key == rendering.Feature.Name);
            measurements.AddRange(RenderHarness.Differences(RenderDiff.Compare(against.Pdf, rendering.Pdf))
                .Select(r => new Measurement(filee!.Name, rendering.Key, r with { Name = RenderThresholds.Versus + r.Name })));
        }

        Publish(filee!.Name, measurements, thresholds, candidate);
        var regressions = measurements.Where(m => thresholds.Judge(m) == Verdict.Regression).ToList();
        Assert.True(regressions.Count == 0, "Render quality regressions:\n" + string.Join("\n", regressions.Select(m => $"{m.Feature}: {m.Result}")));
    }

    [Fact]
    public async Task Rhwp_compared_with_itself_is_identical()
    {
        var rhwp = Rhwp();
        var ct = TestContext.Current.CancellationToken;
        var features = RenderFeatures.All.Where(f => f.Name is "tables" or "script-ko").ToList();

        var first = await RenderHarness.RenderAsync(rhwp, features, fx.NewFolder(), withHwp: false, ct);
        var second = await RenderHarness.RenderAsync(rhwp, features, fx.NewFolder(), withHwp: false, ct);

        foreach (var (a, b) in first.Zip(second))
        {
            var diff = RenderDiff.Compare(a.Pdf, b.Pdf);
            TestContext.Current.TestOutputHelper?.WriteLine($"{a.Key}: {diff}");
            Assert.True(diff.SamePageCount && diff.LineAgreement == 1 && diff.BaselineAgreement == 1 && diff.MinSsim > 0.9999, $"{a.Key}: {diff}");
        }
    }

    [Fact]
    public async Task Perturbed_renderings_are_caught()
    {
        var rhwp = Rhwp();
        var ct = TestContext.Current.CancellationToken;
        var feature = RenderFeatures.All.Single(f => f.Name == "line-spacing-percent");
        var original = Assert.Single(await RenderHarness.RenderAsync(rhwp, [feature], fx.NewFolder(), withHwp: false, ct));

        // The same pages moved down by 2 pt: every line keeps its text, none its baseline, and the pictures differ.
        var shifted = RenderDiff.Compare(original.Pdf, Shift(original.Pdf, 2));
        TestContext.Current.TestOutputHelper?.WriteLine($"shifted: {shifted}");
        Assert.True(shifted.SamePageCount);
        Assert.Equal(1, shifted.LineAgreement);
        Assert.Equal(0, shifted.BaselineAgreement);
        Assert.True(shifted.MinSsim < 0.95, shifted.ToString());
        Assert.Equal(0, PdfChecks.BaselinesMatch([.. PdfLines.Read(Shift(original.Pdf, 2)).Select(l => l.Baseline)], [.. PdfLines.Read(original.Pdf).Select(l => l.Baseline)]).Value);

        // A wider left margin: the lines break elsewhere.
        var narrower = Assert.Single(await RenderHarness.RenderAsync(rhwp, [feature], fx.NewFolder(), withHwp: false, ct, WidenLeftMargin));
        var rewrapped = RenderDiff.Compare(original.Pdf, narrower.Pdf);
        TestContext.Current.TestOutputHelper?.WriteLine($"re-wrapped: {rewrapped}");
        Assert.True(rewrapped.LineAgreement < 0.5, rewrapped.ToString());
        Assert.True(rewrapped.MinSsim < 0.9, rewrapped.ToString());
    }

    // ───────────────────────── Perturbations ─────────────────────────

    /// <summary>Every page of <paramref name="pdf"/> drawn <paramref name="points"/> lower on a page of the same size.</summary>
    internal static byte[] Shift(byte[] pdf, double points)
    {
        using var source = new MemoryStream(pdf);
        using var form = XPdfForm.FromStream(source);
        using var output = new PdfDocument();
        for (var i = 0; i < form.PageCount; i++)
        {
            form.PageNumber = i + 1;
            var page = output.AddPage();
            page.Width = XUnit.FromPoint(form.PointWidth);
            page.Height = XUnit.FromPoint(form.PointHeight);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(form, 0, points, form.PointWidth, form.PointHeight);
        }
        using var result = new MemoryStream();
        output.Save(result);
        return result.ToArray();
    }

    /// <summary>A copy of an HWPX whose left margin is 20 mm wider, so its lines are shorter.</summary>
    private static string WidenLeftMargin(string hwpx)
    {
        var copy = Path.Combine(Path.GetDirectoryName(hwpx)!, "narrower.hwpx");
        File.Copy(hwpx, copy, overwrite: true);
        using var zip = ZipFile.Open(copy, ZipArchiveMode.Update);
        var entry = zip.GetEntry("Contents/section0.xml")!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        xml = Regex.Replace(xml, "(<hp:margin[^>]*\\bleft=\")(\\d+)\"", m => $"{m.Groups[1].Value}{int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 5669}\"");
        entry.Delete();
        using (var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false)))
            writer.Write(xml);
        return copy;
    }

    // ───────────────────────── Output ─────────────────────────

    private static void Publish(string renderer, IReadOnlyList<Measurement> measurements, RenderThresholds thresholds, IReadOnlyList<Rendering> renderings)
    {
        var time = TimeSpan.FromTicks(renderings.Sum(r => r.Time.Ticks));
        var title = $"Render quality: {renderer}, {renderings.Count} documents ({time.TotalSeconds:0.0} s rendering)";
        TestContext.Current.TestOutputHelper?.WriteLine(title);
        TestContext.Current.TestOutputHelper?.WriteLine(RenderHarness.Report(measurements, thresholds, markdown: false));
        // The CI's job summary shows the table without opening the test results.
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, $"### {title}\n\n{RenderHarness.Report(measurements, thresholds, markdown: true)}\n");
    }
}
