using System.Numerics.Tensors;

namespace Gallery.Core.Services.Faces;

/// <summary>A face as the grouping steps see it: its photo (two faces in one photo are never the same person) and fingerprint.</summary>
public sealed record GroupingFace(long Id, long AssetId, float[] Embedding);

/// <summary>The searches behind matching and grouping (docs/faces.md, "Matching and grouping"). Compares every pair, using SIMD.</summary>
public static class FaceGrouping
{
    /// <summary>
    /// For each probe, up to <paramref name="k"/> most similar references at or above <paramref name="min"/>, most
    /// similar first, as (reference index, similarity). <paramref name="parallel"/>: where the work runs (see
    /// <see cref="BackgroundCpu"/>); the thread pool by default.
    /// </summary>
    public static (int Index, float Similarity)[][] Nearest(IReadOnlyList<float[]> probes, IReadOnlyList<float[]> references, int k, float min,
        ParallelOptions? parallel = null)
    {
        var result = new (int, float)[probes.Count][];
        Parallel.For(0, probes.Count, parallel ?? new ParallelOptions(), i =>
        {
            var top = new List<(int Index, float Similarity)>(k + 1);
            var p = probes[i];
            for (var j = 0; j < references.Count; j++)
            {
                if (ReferenceEquals(references[j], p)) continue; // a face is not its own neighbour
                var s = TensorPrimitives.Dot(p, references[j]);
                if (s < min || (top.Count == k && s <= top[^1].Similarity)) continue;
                var at = top.Count;
                while (at > 0 && top[at - 1].Similarity < s) at--;
                top.Insert(at, (j, s));
                if (top.Count > k) top.RemoveAt(k);
            }
            result[i] = top.ToArray();
        });
        return result;
    }

    /// <summary>
    /// Average-link grouping: join groups, most similar pair first, while their faces reach <paramref name="threshold"/> on
    /// average, and never when they share a photo. The average similarity of two groups is the dot product of their summed
    /// fingerprints divided by both sizes, so no table of all pairs is needed. Only pairs of groups whose faces are among
    /// each other's <paramref name="neighbours"/> nearest are considered. Returns groups of indexes into
    /// <paramref name="faces"/>, largest first.
    /// </summary>
    public static List<List<int>> Group(IReadOnlyList<GroupingFace> faces, float threshold, int neighbours = 20, ParallelOptions? parallel = null)
    {
        var n = faces.Count;
        if (n == 0) return new();
        var vectors = faces.Select(f => f.Embedding).ToList();
        var near = Nearest(vectors, vectors, neighbours, threshold, parallel);

        var parent = Enumerable.Range(0, n).ToArray();
        var sums = vectors.Select(v => (float[])v.Clone()).ToArray();
        var counts = Enumerable.Repeat(1, n).ToArray();
        var photos = faces.Select(f => new HashSet<long> { f.AssetId }).ToArray();
        var links = new HashSet<int>[n]; // faces that each group's faces are near
        for (var i = 0; i < n; i++) links[i] = new HashSet<int>(near[i].Select(x => x.Index));
        for (var i = 0; i < n; i++) foreach (var (j, _) in near[i]) links[j].Add(i);

        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        float Average(int a, int b) => TensorPrimitives.Dot(sums[a], sums[b]) / ((float)counts[a] * counts[b]);

        var queue = new PriorityQueue<(int A, int B), float>(); // most similar first, so priorities are negative
        for (var i = 0; i < n; i++)
            foreach (var (j, s) in near[i])
                if (i < j || !near[j].Any(x => x.Index == i)) queue.Enqueue((i, j), -s);

        while (queue.TryDequeue(out var pair, out var negated))
        {
            int a = Find(pair.A), b = Find(pair.B);
            if (a == b) continue;
            var s = Average(a, b);
            if (s < threshold) continue;
            if (Math.Abs(s + negated) > 1e-5f) { queue.Enqueue((a, b), -s); continue; } // groups changed since it was queued
            if (photos[a].Overlaps(photos[b])) continue;
            if (counts[a] < counts[b]) (a, b) = (b, a); // the larger group takes in the smaller
            parent[b] = a;
            TensorPrimitives.Add(sums[a], sums[b], sums[a]);
            counts[a] += counts[b];
            photos[a].UnionWith(photos[b]);
            // The joined group's similarity to the smaller side's neighbours changed most, so queue them again. Pairs
            // already queued for the larger side are worked out again when they come up.
            foreach (var k in links[b])
            {
                var c = Find(k);
                if (c != a) queue.Enqueue((a, c), -Average(a, c));
            }
            links[a].UnionWith(links[b]);
            links[b] = new HashSet<int>();
            photos[b] = new HashSet<long>();
        }

        return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.ToList()).OrderByDescending(g => g.Count).ToList();
    }
}
