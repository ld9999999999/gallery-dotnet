namespace Gallery.Core.Services;

/// <summary>
/// Lays out up to <see cref="MaxPhotos"/> photos in one picture, uncropped. A layout is rows and columns nested inside
/// each other: photos side by side share a height, and stacked photos share a width. Every photo keeps its shape, the
/// gaps are even, and the picture's height follows from its width.
/// <para>
/// A good layout has a pleasant overall shape, photos of about the same size and few levels of nesting. For every group
/// of photos the search keeps the best layout at each of a range of shapes, then builds bigger groups from those, which
/// takes milliseconds even for nine photos. Photos of the same shape are placed in the given order, read left to right
/// and top to bottom.
/// </para>
/// <para>
/// <see cref="Arrange"/> returns arrangements in the order "Rearrange" steps through them: the best first, then the other
/// good kinds of layout, then the same ones again with photos of the same shape swapped.
/// </para>
/// </summary>
public static class Collage
{
    public const int MaxPhotos = 9;

    /// <summary>The widest picture allowed, in pixels.</summary>
    public const int MaxWidth = 10000;

    /// <summary>The most pixels in one picture (about 200 MB to draw).</summary>
    public const long MaxPixels = 64_000_000;

    /// <param name="Width">The whole picture's width in pixels.</param>
    /// <param name="Gap">The space between photos and around the edge, in pixels; 0 makes photos touch.</param>
    /// <param name="Border">The width of the frame around each photo, in pixels; 0 for none.</param>
    public sealed record Options(int Width, int Gap = 0, int Border = 0);

    /// <summary>Where photo <paramref name="Photo"/> (its index in the given list) goes: the photo itself, inside its border.</summary>
    public sealed record Tile(int Photo, int X, int Y, int Width, int Height);

    /// <param name="Tiles">One per photo, in the order given.</param>
    public sealed record Layout(int Width, int Height, IReadOnlyList<Tile> Tiles);

    /// <summary>Where each photo goes, for any size, spacing and borders. <see cref="At"/> works out the pixels.</summary>
    public sealed class Arrangement
    {
        internal Arrangement(Node tree, bool mirrorAcross, bool mirrorDown) { Tree = tree; MirrorAcross = mirrorAcross; MirrorDown = mirrorDown; }
        internal Node Tree { get; }
        internal bool MirrorAcross { get; }
        internal bool MirrorDown { get; }

        /// <summary>The layout at this width, spacing and border; null if some photo gets no room.</summary>
        public Layout? At(Options o) => Solve(Tree, o, MirrorAcross, MirrorDown);
    }

    /// <summary>"Rearrange" offers layouts whose cost is within this much of the best one's.</summary>
    private const double GoodEnough = 0.6;
    private const int MostShapes = 8;
    /// <summary>How many times "Rearrange" cycles through the layouts, swapping photos of the same shape each time.</summary>
    private const int MostRounds = 6;

    /// <summary>
    /// Layouts are ranked at this size, without gaps or borders. Because the ranking ignores the chosen size, spacing
    /// and borders, changing those keeps the same arrangement on screen.
    /// </summary>
    private static readonly Options Ranking = new(10000);

    /// <summary>
    /// Arrangements for photos with these aspect ratios (width / height as shown), in the order "Rearrange" offers them,
    /// best first.
    /// </summary>
    public static IReadOnlyList<Arrangement> Arrange(IReadOnlyList<double> aspects)
    {
        var shapes = Shapes(aspects, Ranking);
        var classes = SameShape(aspects);
        var classOf = new int[aspects.Count];
        for (var c = 0; c < classes.Count; c++) foreach (var p in classes[c]) classOf[p] = c;
        // Reordered rows or a mirror image barely look different, so keep one layout of each kind.
        var best = shapes[0].Cost;
        var good = shapes.DistinctBy(s => Kind(s.Node, classOf))
            .Where((s, i) => i < 2 || s.Cost <= best + GoodEnough).Take(MostShapes).ToList();

        var largest = classes.Max(c => c.Count);
        var rounds = Math.Min(MostRounds, Factorial(largest));
        var list = new List<Arrangement>();
        var seen = new HashSet<string>();
        for (var round = 0; round < rounds; round++)
            foreach (var s in good)
            {
                var tree = round == 0 ? s.Node : Swapped(s.Node, s.Layout, classes, aspects, round);
                if (Solve(tree, Ranking, s.MirrorAcross, s.MirrorDown) is { } layout && seen.Add(Key(layout)))
                    list.Add(new Arrangement(tree, s.MirrorAcross, s.MirrorDown));
            }
        return list;
    }

    /// <summary>The layouts <see cref="Arrange"/> offers, at this size, spacing and border. Layouts that don't fit are dropped.</summary>
    public static IReadOnlyList<Layout> Layouts(IReadOnlyList<double> aspects, Options o) =>
        Arrange(aspects).Select(a => a.At(o)).OfType<Layout>().ToList();

    /// <summary>A layout and its cost. The search tree, mirrored as given, has the photos in order.</summary>
    internal sealed record Shape(double Cost, Layout Layout, Node Node, bool MirrorAcross, bool MirrorDown);

    /// <summary>Every different layout found, cheapest first, with photos of the same shape in the given order.</summary>
    internal static List<Shape> Shapes(IReadOnlyList<double> aspects, Options o)
    {
        var n = aspects.Count;
        if (n == 0 || n > MaxPhotos) throw new ArgumentOutOfRangeException(nameof(aspects), $"1 to {MaxPhotos} photos");
        if (aspects.Any(a => !(a > 0) || !double.IsFinite(a))) throw new ArgumentOutOfRangeException(nameof(aspects));
        var target = TargetAspect(aspects);
        var classes = SameShape(aspects);
        var shapes = new List<Shape>();
        var seen = new HashSet<string>();
        foreach (var node in Search(aspects, target))
            for (var flip = 0; flip < 4; flip++)
            {
                bool across = (flip & 1) != 0, down = (flip & 2) != 0;
                if (Solve(node, o, across, down) is not { } found) continue;
                var tree = Remap(node, InOrder(found, classes), aspects);
                if (Solve(tree, o, across, down) is not { } layout || !seen.Add(Key(layout))) continue;
                shapes.Add(new Shape(Cost(layout, tree.Levels, target), layout, tree, across, down));
            }
        shapes.Sort((x, y) => x.Cost.CompareTo(y.Cost));
        return shapes;
    }

    /// <summary>
    /// The kind of layout, ignoring the order of its rows and columns. Layouts of the same kind have the same rows and
    /// columns with the same photo shapes, in some order.
    /// </summary>
    private static string Kind(Node node, int[] classOf)
    {
        if (node.Photo >= 0) return classOf[node.Photo].ToString();
        var parts = new List<string>();
        void Collect(Node c)
        {
            if (c.Photo < 0 && c.Across == node.Across) { Collect(c.A!); Collect(c.B!); }
            else parts.Add(Kind(c, classOf));
        }
        Collect(node.A!);
        Collect(node.B!);
        parts.Sort(StringComparer.Ordinal);
        return (node.Across ? "row(" : "column(") + string.Join(',', parts) + ")";
    }

    // ---------------------------------------------------------------- search

    /// <summary>A layout of some of the photos: one photo, or two layouts side by side (across) or one above the other.</summary>
    internal sealed class Node
    {
        public int Photo = -1;          // a single photo
        public bool Across;             // side by side, else stacked
        public Node? A, B;              // A is left of / above B
        public double Aspect;           // width / height, without gaps or borders
        public double MinArea, MaxArea; // of its photos, with the layout scaled to height 1
        public int Levels;              // nesting depth: 0 for a photo, 1 for a row, 2 for a grid
        public int Set;                 // its photos, one bit each
        public int OutOfOrder;          // pairs of its photos placed in the opposite of the given order

        public Node(int photo, double aspect) { Photo = photo; Aspect = aspect; MinArea = MaxArea = aspect; Set = 1 << photo; }
        public Node() { }
    }

    /// <summary>The step size (in log aspect ratio) between the shapes the search keeps for each group of photos.</summary>
    private const double ShapeStep = 0.12;
    private const int ShapeBins = 60; // ln aspect from -3.6 to 3.6: 1:36 to 36:1
    private const int KeptAtTheTop = 400;

    /// <summary>The cheapest layouts of all the photos found, without gaps or borders.</summary>
    private static List<Node> Search(IReadOnlyList<double> aspects, double target)
    {
        var n = aspects.Count;
        var full = (1 << n) - 1;
        // For each group of photos (a bit set), the best layout found for each shape.
        var kept = new Node?[full + 1][];
        for (var i = 0; i < n; i++) kept[1 << i] = [new Node(i, aspects[i])];
        if (n == 1) return [kept[1][0]!];
        var top = new List<(double Cost, Node Node)>();
        foreach (var set in Enumerable.Range(1, full).Where(s => (s & (s - 1)) != 0).OrderBy(BitCount))
        {
            var bins = new Node?[ShapeBins];
            var binCost = new double[ShapeBins];
            var low = set & -set;
            // Try each split of the group into two parts once. The part with the lowest photo comes first (left/above).
            for (var a = (set - 1) & set; a > 0; a = (a - 1) & set)
            {
                if ((a & low) == 0) continue;
                var b = set ^ a;
                foreach (var na in kept[a]) if (na is not null)
                foreach (var nb in kept[b]) if (nb is not null)
                for (var across = 0; across < 2; across++)
                {
                    var node = Join(na, nb, across == 1);
                    if (set == full)
                    {
                        var cost = Cost(node.Aspect, node.MaxArea / node.MinArea, node.Levels, target) + Order(node.OutOfOrder);
                        if (top.Count < KeptAtTheTop || cost < top[^1].Cost) Insert(top, (cost, node));
                    }
                    else
                    {
                        var bin = Math.Clamp((int)Math.Floor(Math.Log(node.Aspect) / ShapeStep) + ShapeBins / 2, 0, ShapeBins - 1);
                        var cost = Balance(node.MaxArea / node.MinArea) + Nesting(node.Levels) + Order(node.OutOfOrder);
                        if (bins[bin] is null || cost < binCost[bin]) { bins[bin] = node; binCost[bin] = cost; }
                    }
                }
            }
            kept[set] = bins;
        }
        return top.Select(t => t.Node).ToList();
    }

    private static int BitCount(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    private static void Insert(List<(double Cost, Node Node)> list, (double Cost, Node Node) item)
    {
        var i = list.BinarySearch(item, Comparer<(double Cost, Node Node)>.Create((x, y) => x.Cost.CompareTo(y.Cost)));
        list.Insert(i < 0 ? ~i : i, item);
        if (list.Count > KeptAtTheTop) list.RemoveAt(list.Count - 1);
    }

    private static Node Join(Node a, Node b, bool across)
    {
        var node = new Node { A = a, B = b, Across = across, Set = a.Set | b.Set };
        if (across)
        {
            // Both at height 1: the widths add up and the photo areas stay the same.
            node.Aspect = a.Aspect + b.Aspect;
            node.MinArea = Math.Min(a.MinArea, b.MinArea);
            node.MaxArea = Math.Max(a.MaxArea, b.MaxArea);
        }
        else
        {
            // Scale both to width 1 (by 1 / aspect), then scale the result back to height 1.
            var height = 1 / a.Aspect + 1 / b.Aspect;
            node.Aspect = 1 / height;
            var s = height * height;
            node.MinArea = Math.Min(a.MinArea / (a.Aspect * a.Aspect), b.MinArea / (b.Aspect * b.Aspect)) / s;
            node.MaxArea = Math.Max(a.MaxArea / (a.Aspect * a.Aspect), b.MaxArea / (b.Aspect * b.Aspect)) / s;
        }
        // A row inside a row is still one row, so add a level only when the direction changes.
        int Within(Node c) => c.Photo >= 0 ? 0 : c.Across == across ? c.Levels - 1 : c.Levels;
        node.Levels = 1 + Math.Max(Within(a), Within(b));
        // A's photos come before B's; count the ones that were given after one of B's.
        node.OutOfOrder = a.OutOfOrder + b.OutOfOrder;
        for (var rest = b.Set; rest != 0; rest &= rest - 1)
            node.OutOfOrder += BitCount(a.Set & ~((rest & -rest) * 2 - 1));
        return node;
    }

    // ---------------------------------------------------------------- layout cost

    /// <summary>
    /// The target overall shape, halfway (in log terms) between square and the photos' typical shape. Landscape photos
    /// make a slightly landscape picture, and portrait photos a slightly portrait one.
    /// </summary>
    private static double TargetAspect(IReadOnlyList<double> aspects)
    {
        var typical = Math.Exp(aspects.Average(Math.Log));
        return Math.Sqrt(Math.Clamp(typical, 0.5, 2));
    }

    private static double Cost(double aspect, double areaRatio, int levels, double target) =>
        ShapeCost(aspect, target) + Balance(areaRatio) + Nesting(levels);

    private static double Cost(Layout layout, int levels, double target)
    {
        var areas = layout.Tiles.Select(t => (double)t.Width * t.Height).ToList();
        return Cost((double)layout.Width / layout.Height, areas.Max() / areas.Min(), levels, target)
            + Order(OutOfOrder(layout)) + LargerFirst(layout);
    }

    /// <summary>How far the picture's shape is from the target. The cost rises faster beyond 2.5 : 1.</summary>
    private static double ShapeCost(double aspect, double target)
    {
        var off = Math.Abs(Math.Log(aspect / target));
        var extreme = Math.Max(0, Math.Abs(Math.Log(aspect)) - Math.Log(2.5));
        return off + 2 * extreme;
    }

    /// <summary>How unequal the photo sizes are, from the log of the largest photo's area over the smallest's.</summary>
    private static double Balance(double areaRatio) => 0.8 * 0.5 * Math.Log(areaRatio);

    /// <summary>Rows and columns (2 levels) are free; each extra level of nesting costs a little.</summary>
    private static double Nesting(int levels) => 0.15 * Math.Max(0, levels - 2);

    /// <summary>
    /// A small preference for keeping photos in the given order, read left to right and top to bottom. It only breaks
    /// ties between layouts that are otherwise about equal.
    /// </summary>
    private static double Order(int outOfOrder) => 0.002 * outOfOrder;

    /// <summary>The reading order of photos: by rows (top edges within 1% of the height count as the same row), then left to right.</summary>
    private static List<Tile> Reading(Layout layout) =>
        layout.Tiles.OrderBy(t => (int)Math.Round(t.Y * 100.0 / layout.Height)).ThenBy(t => t.X).ToList();

    /// <summary>Counts pairs of photos that read in the opposite of the given order.</summary>
    private static int OutOfOrder(Layout layout)
    {
        var read = Reading(layout).Select(t => t.Photo).ToList();
        var pairs = 0;
        for (var i = 0; i < read.Count; i++)
            for (var j = i + 1; j < read.Count; j++)
                if (read[i] > read[j]) pairs++;
        return pairs;
    }

    /// <summary>Favors the mirror image that reads larger photos first, so the first given photo tends to be the largest.</summary>
    private static double LargerFirst(Layout layout)
    {
        var read = Reading(layout).Select(t => (double)t.Width * t.Height).ToList();
        var pairs = 0;
        for (var i = 0; i < read.Count; i++)
            for (var j = i + 1; j < read.Count; j++)
                if (read[i] < 0.95 * read[j]) pairs++;
        return 0.001 * pairs;
    }

    // ---------------------------------------------------------------- photos of the same shape

    /// <summary>Groups the photos by shape (aspect ratios within 1%), each group in the given order.</summary>
    private static List<List<int>> SameShape(IReadOnlyList<double> aspects)
    {
        var classes = new List<List<int>>();
        for (var i = 0; i < aspects.Count; i++)
        {
            var c = classes.FirstOrDefault(c => Math.Abs(Math.Log(aspects[c[0]] / aspects[i])) < 0.01);
            if (c is null) classes.Add([i]); else c.Add(i);
        }
        return classes;
    }

    /// <summary>Maps each photo to the one that takes its place, so each same-shape group reads in the given order.</summary>
    private static int[] InOrder(Layout layout, List<List<int>> classes) => Placing(layout, classes, _ => null);

    /// <summary>
    /// Puts each same-shape group's photos into the places they had in <paramref name="layout"/> (in reading order),
    /// using the order <paramref name="order"/> gives for the group's size; null keeps the given order.
    /// </summary>
    private static int[] Placing(Layout layout, List<List<int>> classes, Func<int, int[]?> order)
    {
        var rank = new int[layout.Tiles.Count];
        var read = Reading(layout);
        for (var i = 0; i < read.Count; i++) rank[read[i].Photo] = i;
        var map = new int[layout.Tiles.Count];
        foreach (var c in classes)
        {
            var places = c.OrderBy(p => rank[p]).ToList(); // the group's places, in reading order
            var perm = order(c.Count);
            for (var i = 0; i < places.Count; i++) map[places[i]] = c[perm?[i] ?? i];
        }
        return map;
    }

    /// <summary>The tree with photo <c>p</c> replaced by <c>map[p]</c>.</summary>
    private static Node Remap(Node node, int[] map, IReadOnlyList<double> aspects)
    {
        if (node.Photo >= 0) return new Node(map[node.Photo], aspects[map[node.Photo]]);
        return new Node { A = Remap(node.A!, map, aspects), B = Remap(node.B!, map, aspects), Across = node.Across, Levels = node.Levels };
    }

    /// <summary>
    /// For round <paramref name="round"/> of "Rearrange", swaps the photos within each same-shape group. A given round
    /// always produces the same swap.
    /// </summary>
    private static Node Swapped(Node node, Layout layout, List<List<int>> classes, IReadOnlyList<double> aspects, int round) =>
        Remap(node, Placing(layout, classes, size => Permutation(size, (int)((long)round * 104729 % Factorial(size)))), aspects);

    /// <summary>
    /// The <paramref name="index"/>th ordering of 0 … size − 1. The caller multiplies the round by a large prime, modulo
    /// size!, so each round gets a different, well-mixed ordering.
    /// </summary>
    private static int[] Permutation(int size, int index)
    {
        var left = Enumerable.Range(0, size).ToList();
        var perm = new int[size];
        for (var i = 0; i < size; i++)
        {
            var f = Factorial(size - 1 - i);
            perm[i] = left[index / f];
            left.RemoveAt(index / f);
            index %= f;
        }
        return perm;
    }

    private static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    // ---------------------------------------------------------------- exact sizes

    /// <summary>
    /// A layout's height for a given width is <c>Slope × width + Offset</c>, since gaps and borders stay fixed while the
    /// photos scale. A bordered photo, for example, is (width − 2·border) / aspect + 2·border tall.
    /// </summary>
    private readonly record struct Line(double Slope, double Offset)
    {
        public double HeightFor(double width) => Slope * width + Offset;
        public double WidthFor(double height) => (height - Offset) / Slope;
    }

    private static Line LineOf(Node node, double gap, double border)
    {
        if (node.Photo >= 0) return new Line(1 / node.Aspect, 2 * border - 2 * border / node.Aspect);
        var a = LineOf(node.A!, gap, border);
        var b = LineOf(node.B!, gap, border);
        if (!node.Across) return new Line(a.Slope + b.Slope, a.Offset + b.Offset + gap);
        // Side by side at height h: width = (h − Offset_a) / Slope_a + (h − Offset_b) / Slope_b + gap.
        var slope = 1 / (1 / a.Slope + 1 / b.Slope);
        return new Line(slope, slope * (a.Offset / a.Slope + b.Offset / b.Slope - gap));
    }

    /// <summary>The layout at the given width, mirrored if asked. Null if some photo would get no room.</summary>
    private static Layout? Solve(Node root, Options o, bool mirrorAcross, bool mirrorDown)
    {
        double gap = Math.Max(0, o.Gap), border = Math.Max(0, o.Border);
        var inner = o.Width - 2 * gap;
        if (inner <= 0) return null;
        var height = LineOf(root, gap, border).HeightFor(inner);
        if (!(height > 0)) return null;
        var tiles = new List<Tile>();
        var fits = true;
        // Each part gets its edges. A split only computes the edge between its two parts, so the last part's far edge is
        // exactly the parent's. Edges are rounded rather than sizes, which keeps neighbours exactly one gap apart.
        void Place(Node node, double left, double top, double right, double bottom)
        {
            if (node.Photo >= 0)
            {
                int x0 = Px(left + border), x1 = Px(right - border), y0 = Px(top + border), y1 = Px(bottom - border);
                if (x1 - x0 < 1 || y1 - y0 < 1) fits = false;
                tiles.Add(new Tile(node.Photo, x0, y0, x1 - x0, y1 - y0));
                return;
            }
            var (first, second) = (node.A!, node.B!);
            var line = LineOf(first, gap, border);
            if (node.Across)
            {
                var w = line.WidthFor(bottom - top);
                if (mirrorAcross) { Place(second, left, top, right - w - gap, bottom); Place(first, right - w, top, right, bottom); }
                else { Place(first, left, top, left + w, bottom); Place(second, left + w + gap, top, right, bottom); }
            }
            else
            {
                var h = line.HeightFor(right - left);
                if (mirrorDown) { Place(second, left, top, right, bottom - h - gap); Place(first, left, bottom - h, right, bottom); }
                else { Place(first, left, top, right, top + h); Place(second, left, top + h + gap, right, bottom); }
            }
        }
        Place(root, gap, gap, gap + inner, gap + height);
        return fits ? new Layout(o.Width, Px(height + 2 * gap), tiles.OrderBy(t => t.Photo).ToList()) : null;
    }

    /// <summary>Rounds to the nearest pixel, halves up, so adding whole pixels before or after rounding gives the same result.</summary>
    private static int Px(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>Two layouts with the same key put the same photos in the same places.</summary>
    private static string Key(Layout l) => $"{l.Height}:" + string.Join(';', l.Tiles.Select(t => $"{t.X},{t.Y},{t.Width},{t.Height}"));
}
