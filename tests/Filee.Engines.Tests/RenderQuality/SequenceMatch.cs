// Longest common subsequence of two sequences (characters of a text, lines of a document) with Myers' O(ND) diff, so
// documents that mostly agree are matched quickly whatever their length. Past a number of differences the matching
// stops and falls back to pairing equal items without regard to their order: the result is then marked approximate.

namespace Filee.Engines.Tests.RenderQuality;

internal static class SequenceMatch
{
    /// <summary>
    /// Index pairs (in <paramref name="a"/>, in <paramref name="b"/>) of a longest common subsequence, in order. With
    /// more than <paramref name="maxDifferences"/> insertions and deletions, equal items are paired in order of
    /// occurrence instead and <c>Exact</c> is false.
    /// </summary>
    public static (List<(int A, int B)> Pairs, bool Exact) Match<T>(IReadOnlyList<T> a, IReadOnlyList<T> b,
        IEqualityComparer<T>? comparer = null, int maxDifferences = 2000) where T : notnull
    {
        comparer ??= EqualityComparer<T>.Default;
        int n = a.Count, m = b.Count;
        var limit = Math.Min(n + m, maxDifferences);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        // The furthest x on each diagonal -d..d before step d: the trace grows with d², not with the length.
        var trace = new List<int[]>();
        for (var d = 0; d <= limit; d++)
        {
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && comparer.Equals(a[x], b[y]))
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                    return (Backtrack(trace, n, m), true);
            }
        }
        return (InOrderOfOccurrence(a, b, comparer), false);
    }

    private static List<(int A, int B)> Backtrack(List<int[]> trace, int n, int m)
    {
        var pairs = new List<(int A, int B)>();
        int x = n, y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            int startX = 0, startY = 0, previousX = 0, previousY = 0;
            if (d > 0)
            {
                var before = trace[d];
                int At(int diagonal) => before[diagonal + d];
                var k = x - y;
                var down = k == -d || (k != d && At(k - 1) < At(k + 1));
                var previousK = down ? k + 1 : k - 1;
                previousX = At(previousK);
                previousY = previousX - previousK;
                (startX, startY) = down ? (previousX, previousY + 1) : (previousX + 1, previousY);
            }
            // The snake of equal items that ends at (x, y).
            while (x > startX && y > startY)
            {
                pairs.Add((x - 1, y - 1));
                x--;
                y--;
            }
            x = previousX;
            y = previousY;
        }
        pairs.Reverse();
        return pairs;
    }

    private static List<(int A, int B)> InOrderOfOccurrence<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T> comparer)
        where T : notnull
    {
        var waiting = new Dictionary<T, Queue<int>>(comparer);
        for (var j = 0; j < b.Count; j++)
        {
            if (!waiting.TryGetValue(b[j], out var queue))
                waiting[b[j]] = queue = new Queue<int>();
            queue.Enqueue(j);
        }
        var pairs = new List<(int A, int B)>();
        for (var i = 0; i < a.Count; i++)
            if (waiting.TryGetValue(a[i], out var queue) && queue.Count > 0)
                pairs.Add((i, queue.Dequeue()));
        return pairs;
    }
}
