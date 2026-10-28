// Differential compare of two renderings of one document (rhwp and Filee's layout engine, or 한글's own PDF and a
// renderer): page count, line breaks, baselines and per-page SSIM of the pages drawn by PDFium at 100 dpi.
//
// Line breaks are compared as lines: the text of each line without white space, matched in order between the two
// PDFs (longest common subsequence). A line agrees when the other PDF has a line with the same text at that point of
// the document, so it agrees in both its first and its last character; a page break moved by one line costs nothing
// more than the lines that moved. Matched lines also compare their baselines (same page, within ±0.5 pt).

using System.Globalization;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>How alike two renderings are.</summary>
/// <param name="LineAgreement">Share of lines with the same text in both, of the longer document's lines.</param>
/// <param name="BaselineAgreement">Share of lines that also sit on the same page and baseline (±0.5 pt).</param>
/// <param name="PageSsim">SSIM of each page both have.</param>
/// <param name="Exact">False when the documents differed too much for an exact line matching (then approximate).</param>
internal sealed record DiffResult(int ReferencePages, int CandidatePages, int ReferenceLines, int CandidateLines,
    double LineAgreement, double BaselineAgreement, IReadOnlyList<double> PageSsim, bool Exact)
{
    public bool SamePageCount => ReferencePages == CandidatePages;
    public double MinSsim => PageSsim.Count == 0 ? (ReferencePages == 0 && CandidatePages == 0 ? 1 : 0) : PageSsim.Min();
    public double MeanSsim => PageSsim.Count == 0 ? MinSsim : PageSsim.Average();

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"pages {ReferencePages}/{CandidatePages}, lines {LineAgreement:P1}, baselines {BaselineAgreement:P1}, SSIM min {MinSsim:0.####} mean {MeanSsim:0.####}{(Exact ? "" : " (approximate)")}");
}

internal static class RenderDiff
{
    public const int Dpi = 100;

    /// <summary>
    /// Compares <paramref name="candidate"/> with <paramref name="reference"/>; pages already drawn at <see cref="Dpi"/>
    /// can be passed in.
    /// </summary>
    public static DiffResult Compare(byte[] reference, byte[] candidate, IReadOnlyList<GreyImage>? referencePages = null,
        IReadOnlyList<GreyImage>? candidatePages = null, double baselineTolerance = 0.5)
    {
        var (lines, baselines, referenceLines, candidateLines, exact) = CompareLines(PdfLines.Read(reference), PdfLines.Read(candidate), baselineTolerance);
        referencePages ??= GreyImage.Pages(reference, Dpi);
        candidatePages ??= GreyImage.Pages(candidate, Dpi);
        var ssim = new List<double>();
        for (var i = 0; i < Math.Min(referencePages.Count, candidatePages.Count); i++)
            ssim.Add(Ssim.Compare(referencePages[i], candidatePages[i]).Ssim);
        return new DiffResult(referencePages.Count, candidatePages.Count, referenceLines, candidateLines, lines, baselines, ssim, exact);
    }

    /// <summary>Line-break and baseline agreement of two documents' lines (see the file comment).</summary>
    public static (double Lines, double Baselines, int ReferenceLines, int CandidateLines, bool Exact) CompareLines(
        IReadOnlyList<TextLine> reference, IReadOnlyList<TextLine> candidate, double baselineTolerance = 0.5)
    {
        var total = Math.Max(reference.Count, candidate.Count);
        if (total == 0)
            return (1, 1, 0, 0, true);
        var (pairs, exact) = SequenceMatch.Match(reference.Select(l => l.Key).ToList(), candidate.Select(l => l.Key).ToList(), StringComparer.Ordinal);
        var placed = pairs.Count(p => reference[p.A].Page == candidate[p.B].Page
                                      && Math.Abs(reference[p.A].Baseline - candidate[p.B].Baseline) <= baselineTolerance);
        return ((double)pairs.Count / total, (double)placed / total, reference.Count, candidate.Count, exact);
    }
}
