using System.Data;
using System.Diagnostics;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.Faces;

/// <summary>A person with the number of items and faces that count, and the face to show for them.</summary>
public sealed record PersonSummary(long Id, string? Name, bool Hidden, long ItemCount, long FaceCount, long? CoverFaceId, string CreatedAt);

/// <summary>A face to show on screen: its box as fractions of the picture as shown now, and who it is.</summary>
public sealed record FaceInfo(long Id, long AssetId, long ItemId, float X1, float Y1, float X2, float Y2, float Score, float EyePx,
    long? PersonId, string? PersonName, string? AssignedBy);

/// <summary>A face nobody is assigned to, with the person it looks most like (when any looks like it at all).</summary>
public sealed record FaceSuggestion(FaceInfo Face, long? PersonId, string? PersonName, float? Similarity);

/// <summary>What a grouping pass did. Quick: only new faces were matched to people, and nothing was grouped.</summary>
public sealed record GroupingPass(int Considered, int Matched, int NewPeople, int Grouped, double Seconds, string FinishedAt, bool Quick = false);

/// <summary>
/// People (docs/faces.md, Phase 2): groups faces into people in the background, and handles the owner's corrections,
/// the person tags, and the queries for the People screens. Viewers see named people that aren't hidden, counted over
/// the items they may see (<see cref="ItemRepository.ViewerVisible"/>).
/// <para>
/// A pass runs soon after new faces arrive, at most every 10 minutes while faces are still being found, and daily:
/// 1. Each clear, unassigned face joins the person whose faces it matches best.
/// 2. The faces still left are grouped into suggested, unnamed people.
/// A pass leaves faces the owner assigned or removed where they are.
/// </para>
/// <para>
/// Grouping compares every unassigned face with every other one, which takes seconds to minutes for a big library. So
/// when only a few faces are new since the last full pass (a photo turned or edited), a quick pass only matches them to
/// the people that exist. They are grouped with the rest later: once enough new faces have come in, daily, or when asked.
/// Passes run on <see cref="BackgroundCpu"/>, which keeps the thread pool free for web requests.
/// </para>
/// </summary>
public sealed class PeopleService : BackgroundService
{
    public const string TagKey = "person";
    private const string TagSource = "faces";
    private const int Neighbours = 5;          // nearest assigned faces a new face is compared with
    private const int ReferencesPerPerson = 300; // a person's faces used for matching, spread over time
    private const float SuggestBelow = 0.3f;   // suggestions in the unassigned list accept weaker matches than auto matching
    private static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    /// <summary>Most new unassigned faces a quick pass handles. At this many or more, the pass is a full one.</summary>
    internal const int QuickPassLimit = 200;

    private readonly Db _db;
    private readonly FaceService _faces;
    private readonly FaceOptions _options;
    private readonly ILogger<PeopleService> _log;
    private readonly BackgroundCpu? _cpu;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _passLock = new();
    private int _dirty = 1; // run a first pass after startup to catch what the last run left
    private int _asked;
    private int _grouping;
    private long _groupedThrough; // the newest face the last full pass saw; 0 until one has run

    public PeopleService(Db db, FaceService faces, GalleryOptions options, ILogger<PeopleService> log, BackgroundCpu? cpu = null)
    {
        _db = db;
        _faces = faces;
        _options = options.Faces;
        _log = log;
        _cpu = cpu;
        faces.FacesSaved += () => Interlocked.Exchange(ref _dirty, 1);
    }

    public bool Grouping => Volatile.Read(ref _grouping) == 1;

    public GroupingPass? LastPass { get; private set; }

    /// <summary>Runs a pass as soon as possible (Admin → Group faces now, or after corrections).</summary>
    public void RequestPass()
    {
        Interlocked.Exchange(ref _asked, 1);
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Count from startup, so the first pass also waits for the face queue to go quiet (or 10 minutes). Grouping a
        // few early faces would only start suggestions that the rest then join.
        var last = DateTime.UtcNow;
        var lastFull = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
            var now = DateTime.UtcNow;
            var asked = Interlocked.Exchange(ref _asked, 0) == 1;
            var daily = now - lastFull > Daily;
            var due = asked || daily || (Volatile.Read(ref _dirty) == 1 && (!_faces.Activity.Running || now - last > BusyInterval));
            if (!due || !_faces.Enabled) continue;
            Interlocked.Exchange(ref _dirty, 0);
            try
            {
                var full = asked || daily || NewFaces() >= QuickPassLimit;
                var pass = _cpu is null ? await Task.Run(() => RunPass(ct, full), ct) : await _cpu.Run(() => RunPass(ct, full), ct);
                if (!pass.Quick) lastFull = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Grouping faces failed"); }
            last = DateTime.UtcNow;
        }
    }

    // ---------------------------------------------------------------- the pass

    private sealed class PassFace
    {
        public long Id { get; set; }
        public long AssetId { get; set; }
        public long? PersonId { get; set; }
        public string? AssignedBy { get; set; }
        public double Score { get; set; }
        public double EyePx { get; set; }
        public string? TakenAt { get; set; }
    }

    private bool Clear(PassFace f) => f.Score >= _options.MinScore && f.EyePx >= _options.MinEyePx;

    /// <summary>
    /// One matching and grouping pass over the current pack's faces. When <paramref name="full"/> is false, it is a quick
    /// pass if few faces are new since the last full one (see the class), else a full one. Safe to call from several
    /// threads (passes wait in line).
    /// </summary>
    public GroupingPass RunPass(CancellationToken ct = default, bool full = true)
    {
        lock (_passLock)
        {
            Volatile.Write(ref _grouping, 1);
            try { return Pass(ct, full || Volatile.Read(ref _groupedThrough) == 0); }
            finally { Volatile.Write(ref _grouping, 0); }
        }
    }

    /// <summary>Clear, unassigned faces newer than the last full pass.</summary>
    internal int NewFaces()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM live_faces
            WHERE model=@model AND id>@since AND person_id IS NULL AND assigned_by IS NULL AND score>=@minScore AND eye_px>=@minEye
            """, new { model = _faces.Pack.Name, since = Volatile.Read(ref _groupedThrough), minScore = _options.MinScore, minEye = _options.MinEyePx });
    }

    private GroupingPass Pass(CancellationToken ct, bool full)
    {
        var clock = Stopwatch.StartNew();
        var model = _faces.Pack.Name;
        List<PassFace> faces;
        Dictionary<long, HashSet<long>> rejected;
        using (var c = _db.Open())
        {
            faces = c.Query<PassFace>("""
                SELECT lf.id, lf.asset_id, lf.person_id, lf.assigned_by, lf.score, lf.eye_px, i.taken_at
                FROM live_faces lf JOIN items i ON i.id=lf.item_id WHERE lf.model=@model
                """, new { model }).ToList();
            rejected = c.Query<(long FaceId, long PersonId)>("SELECT face_id, person_id FROM face_rejections")
                .GroupBy(r => r.FaceId).ToDictionary(g => g.Key, g => g.Select(r => r.PersonId).ToHashSet());
        }
        var since = full ? 0 : Volatile.Read(ref _groupedThrough);
        var candidates = faces.Where(f => f.Id > since && f.PersonId is null && f.AssignedBy is null && Clear(f)).ToList();
        var parallel = _cpu?.Parallel(ct) ?? new ParallelOptions { CancellationToken = ct };
        var matched = new Dictionary<long, long>(); // face → person
        var newPeople = 0;
        var grouped = 0;
        if (candidates.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            // 1. Join a person: take the 5 nearest assigned faces at or above the threshold. The person with most of them
            //    wins, unless the face was removed from them before or they are already in the photo.
            var references = References(faces);
            if (references.Count > 0)
            {
                var embeddings = LoadEmbeddings(references.Select(r => r.Id).Concat(candidates.Select(f => f.Id)));
                var near = FaceGrouping.Nearest(candidates.Select(f => embeddings[f.Id]).ToList(), references.Select(r => embeddings[r.Id]).ToList(),
                    Neighbours, (float)_options.MatchThreshold, parallel);
                var inPhoto = faces.Where(f => f.PersonId is not null).GroupBy(f => f.AssetId).ToDictionary(g => g.Key, g => g.Select(f => f.PersonId!.Value).ToHashSet());
                var proposals = new List<(PassFace Face, long Person, float Similarity)>();
                for (var i = 0; i < candidates.Count; i++)
                {
                    var no = rejected.GetValueOrDefault(candidates[i].Id);
                    var best = near[i].Select(x => (Person: references[x.Index].PersonId!.Value, x.Similarity))
                        .Where(x => no is null || !no.Contains(x.Person))
                        .GroupBy(x => x.Person)
                        .Select(g => (Person: g.Key, Votes: g.Count(), Best: g.Max(x => x.Similarity)))
                        .OrderByDescending(x => x.Votes).ThenByDescending(x => x.Best)
                        .FirstOrDefault();
                    if (best.Votes > 0) proposals.Add((candidates[i], best.Person, best.Best));
                }
                foreach (var (face, person, _) in proposals.OrderByDescending(p => p.Similarity))
                {
                    var here = inPhoto.TryGetValue(face.AssetId, out var set) ? set : inPhoto[face.AssetId] = new HashSet<long>();
                    if (!here.Add(person)) continue; // two faces of one photo are never one person
                    matched[face.Id] = person;
                }
            }

            // 2. Full passes only: group the faces still left. Groups of MinGroupSize or more become suggested people.
            ct.ThrowIfCancellationRequested();
            var left = candidates.Where(f => !matched.ContainsKey(f.Id)).ToList();
            var groups = new List<List<long>>();
            if (full && left.Count >= _options.MinGroupSize)
            {
                var embeddings = LoadEmbeddings(left.Select(f => f.Id));
                var items = left.Select(f => new GroupingFace(f.Id, f.AssetId, embeddings[f.Id])).ToList();
                groups = FaceGrouping.Group(items, (float)_options.GroupThreshold, parallel: parallel)
                    .Where(g => g.Count >= _options.MinGroupSize).Select(g => g.Select(i => items[i].Id).ToList()).ToList();
            }

            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            // Only change faces that are still unassigned and untouched, so a correction made during the pass wins.
            foreach (var (faceId, person) in matched)
                c.Execute("UPDATE faces SET person_id=@person, assigned_by='auto' WHERE id=@faceId AND person_id IS NULL AND assigned_by IS NULL", new { faceId, person }, tx);
            var now = Clock.NowIso();
            foreach (var g in groups)
            {
                var id = c.ExecuteScalar<long>("INSERT INTO people(name, created_at, updated_at) VALUES(NULL, @now, @now); SELECT last_insert_rowid();", new { now }, tx);
                grouped += c.Execute("UPDATE faces SET person_id=@id, assigned_by='auto' WHERE id IN @g AND person_id IS NULL AND assigned_by IS NULL", new { id, g }, tx);
                newPeople++;
            }
            Tidy(c, tx);
            if (_options.PersonTags) SyncTags(c, tx, null);
            tx.Commit();
        }
        else
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            Tidy(c, tx);
            if (_options.PersonTags) SyncTags(c, tx, null);
            tx.Commit();
        }
        if (full && faces.Count > 0) Volatile.Write(ref _groupedThrough, Math.Max(Volatile.Read(ref _groupedThrough), faces.Max(f => f.Id)));
        if (matched.Count > 0 || newPeople > 0) _db.Touch();
        var pass = new GroupingPass(candidates.Count, matched.Count, newPeople, grouped, clock.Elapsed.TotalSeconds, Clock.NowIso(), !full);
        LastPass = pass;
        if (candidates.Count > 0 && full)
            _log.LogInformation("Faces grouped in {Seconds:0.0}s: {Considered:N0} unassigned faces, {Matched:N0} joined a person, {Grouped:N0} formed {People:N0} new suggestions",
                pass.Seconds, pass.Considered, pass.Matched, pass.Grouped, pass.NewPeople);
        else if (candidates.Count > 0)
            _log.LogInformation("Faces matched in {Seconds:0.0}s: {Considered:N0} new faces, {Matched:N0} joined a person (grouped with the rest later)",
                pass.Seconds, pass.Considered, pass.Matched);
        return pass;
    }

    /// <summary>Assigned faces to match against: up to <see cref="ReferencesPerPerson"/> per person, spread over the years.</summary>
    private static List<PassFace> References(List<PassFace> faces) =>
        faces.Where(f => f.PersonId is not null).GroupBy(f => f.PersonId).SelectMany(g =>
        {
            var list = g.OrderBy(f => f.TakenAt, StringComparer.Ordinal).ToList();
            if (list.Count <= ReferencesPerPerson) return list;
            return Enumerable.Range(0, ReferencesPerPerson).Select(i => list[(int)((long)i * list.Count / ReferencesPerPerson)]);
        }).ToList();

    private Dictionary<long, float[]> LoadEmbeddings(IEnumerable<long> ids)
    {
        var result = new Dictionary<long, float[]>();
        using var c = _db.Open();
        foreach (var chunk in ids.Distinct().Chunk(5000))
            foreach (var (id, bytes) in c.Query<(long, byte[])>("SELECT id, embedding FROM faces WHERE id IN @chunk", new { chunk }))
                result[id] = FaceRepository.FromHalfBytes(bytes);
        return result;
    }

    /// <summary>Removes suggested people with no faces left. Named people stay, so their name can be used again.</summary>
    private static void Tidy(SqliteConnection c, IDbTransaction tx) =>
        c.Execute("DELETE FROM people WHERE name IS NULL AND NOT EXISTS (SELECT 1 FROM faces f WHERE f.person_id=people.id)", transaction: tx);

    /// <summary>
    /// Gives every item with a counted face of a named person the tag "person: Name" (marked as set by the face service),
    /// and removes it from items that lost the face. Leaves person tags added by hand alone. Null: every named person.
    /// </summary>
    private static void SyncTags(SqliteConnection c, IDbTransaction tx, IEnumerable<long>? personIds)
    {
        var people = personIds is null
            ? c.Query<(long Id, string Name)>("SELECT id, name FROM people WHERE name IS NOT NULL", transaction: tx).ToList()
            : c.Query<(long Id, string Name)>("SELECT id, name FROM people WHERE name IS NOT NULL AND id IN @ids", new { ids = personIds.ToList() }, tx).ToList();
        foreach (var (id, name) in people)
        {
            c.Execute("""
                DELETE FROM item_tags WHERE key=@key AND value=@name AND source=@source
                    AND item_id NOT IN (SELECT item_id FROM live_faces WHERE person_id=@id)
                """, new { key = TagKey, name, source = TagSource, id }, tx);
            c.Execute("INSERT OR IGNORE INTO item_tags(item_id, key, value, source) SELECT DISTINCT item_id, @key, @name, @source FROM live_faces WHERE person_id=@id",
                new { key = TagKey, name, source = TagSource, id }, tx);
        }
    }

    private static void DropTags(SqliteConnection c, IDbTransaction tx, string? name)
    {
        if (name is not null) c.Execute("DELETE FROM item_tags WHERE key=@key AND value=@name AND source=@source", new { key = TagKey, name, source = TagSource }, tx);
    }

    // ---------------------------------------------------------------- queries

    private sealed class PersonRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public int Hidden { get; set; }
        public string CreatedAt { get; set; } = "";
        public long ItemCount { get; set; }
        public long FaceCount { get; set; }
        public long? CoverFaceId { get; set; }
    }

    /// <summary>
    /// People with their counts and cover. The cover is the chosen face when it counts (and, for viewers, is visible),
    /// else the clearest face. Most photos first. Viewers get named people that aren't hidden; admins get all, including
    /// hidden ones when asked.
    /// </summary>
    public List<PersonSummary> People(bool forViewers, bool includeHidden = false, long? personId = null)
    {
        var visible = forViewers ? " AND " + ItemRepository.ViewerVisible("vi") : "";
        var who = forViewers ? " AND p.name IS NOT NULL AND p.hidden=0" : includeHidden || personId is not null ? "" : " AND p.hidden=0";
        var one = personId is null ? "" : " AND p.id=@personId";
        using var c = _db.Open();
        // SQLite: with max(), the other plain columns come from the row with the maximum, so cover_face_id is that face.
        return c.Query<PersonRow>($"""
            SELECT p.id, p.name, p.hidden, p.created_at, COUNT(DISTINCT lf.item_id) AS item_count, COUNT(*) AS face_count,
                   max(COALESCE(lf.id=p.cover_face_id, 0) * 1e9 + lf.eye_px * lf.score) AS best_rank, lf.id AS cover_face_id
            FROM people p
            JOIN live_faces lf ON lf.person_id=p.id
            JOIN items vi ON vi.id=lf.item_id
            WHERE 1=1{who}{visible}{one}
            GROUP BY p.id
            ORDER BY item_count DESC, p.id
            """, new { personId }).Select(r => new PersonSummary(r.Id, r.Name, r.Hidden == 1, r.ItemCount, r.FaceCount, r.CoverFaceId, r.CreatedAt)).ToList();
    }

    public PersonSummary? Person(long id, bool forViewers) => People(forViewers, personId: id).FirstOrDefault()
        ?? (forViewers ? null : BarePerson(id));

    /// <summary>A person with no counted faces left (admins can still open, rename or delete them).</summary>
    private PersonSummary? BarePerson(long id)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<PersonRow>("SELECT id, name, hidden, created_at FROM people WHERE id=@id", new { id }) is { } r
            ? new PersonSummary(r.Id, r.Name, r.Hidden == 1, 0, 0, null, r.CreatedAt)
            : null;
    }

    public long? PersonIdByName(string name)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM people WHERE name=@name", new { name = name.Trim() });
    }

    private sealed class FaceRow
    {
        public long Id { get; set; }
        public long AssetId { get; set; }
        public long ItemId { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double Score { get; set; }
        public double EyePx { get; set; }
        public long? PersonId { get; set; }
        public string? PersonName { get; set; }
        public int PersonHidden { get; set; }
        public string? AssignedBy { get; set; }
        public int? ScannedRotation { get; set; }
        public string? RotationTag { get; set; }
    }

    private const string FaceSelect = """
        SELECT lf.id, lf.asset_id, lf.item_id, lf.x1, lf.y1, lf.x2, lf.y2, lf.score, lf.eye_px, lf.person_id, lf.assigned_by,
               p.name AS person_name, COALESCE(p.hidden, 0) AS person_hidden, fs.rotation AS scanned_rotation,
               (SELECT t.value FROM item_tags t WHERE t.item_id=lf.item_id AND t.key='rotate-degrees' LIMIT 1) AS rotation_tag
        FROM live_faces lf
        LEFT JOIN people p ON p.id=lf.person_id
        LEFT JOIN face_scans fs ON fs.asset_id=lf.asset_id
        JOIN items vi ON vi.id=lf.item_id
        """;

    /// <summary>Turns the box to the item's current rotation if it was found at another one (until the photo is searched again).</summary>
    private static FaceInfo ToInfo(FaceRow r, bool forViewers)
    {
        var box = FaceRepository.RotateBox(((float)r.X1, (float)r.Y1, (float)r.X2, (float)r.Y2), Rotation.Parse(r.RotationTag) - (r.ScannedRotation ?? 0));
        // Viewers see named people that aren't hidden; the rest show as a face without a name.
        var shown = !forViewers || (r.PersonName is not null && r.PersonHidden == 0);
        return new FaceInfo(r.Id, r.AssetId, r.ItemId, box.X1, box.Y1, box.X2, box.Y2, (float)r.Score, (float)r.EyePx,
            shown ? r.PersonId : null, shown ? r.PersonName : null, forViewers ? null : r.AssignedBy);
    }

    /// <summary>The faces in an item, left to right. Null when a viewer may not see the item.</summary>
    public List<FaceInfo>? ItemFaces(long itemId, bool forViewers)
    {
        using var c = _db.Open();
        if (forViewers && c.ExecuteScalar<long>($"SELECT COUNT(*) FROM items i WHERE i.id=@itemId AND {ItemRepository.ViewerVisible()}", new { itemId }) == 0) return null;
        return c.Query<FaceRow>(FaceSelect + " WHERE lf.item_id=@itemId ORDER BY lf.x1", new { itemId }).Select(r => ToInfo(r, forViewers)).ToList();
    }

    /// <summary>One face; null when it doesn't count, or a viewer may not see its item.</summary>
    public FaceInfo? Face(long faceId, bool forViewers)
    {
        using var c = _db.Open();
        var visible = forViewers ? " AND " + ItemRepository.ViewerVisible("vi") : "";
        return c.QuerySingleOrDefault<FaceRow>(FaceSelect + $" WHERE lf.id=@faceId{visible}", new { faceId }) is { } r ? ToInfo(r, forViewers) : null;
    }

    /// <summary>A person's faces, newest photos first (admins: for corrections).</summary>
    public (List<FaceInfo> Faces, long Total) FacesOf(long personId, int offset, int limit)
    {
        using var c = _db.Open();
        var total = c.ExecuteScalar<long>("SELECT COUNT(*) FROM live_faces WHERE person_id=@personId", new { personId });
        var rows = c.Query<FaceRow>(FaceSelect + " WHERE lf.person_id=@personId ORDER BY vi.taken_at DESC, lf.id LIMIT @limit OFFSET @offset",
            new { personId, limit = Math.Clamp(limit, 1, 1000), offset = Math.Max(0, offset) });
        return (rows.Select(r => ToInfo(r, false)).ToList(), total);
    }

    /// <summary>
    /// Faces nobody is assigned to, clearest first, each with the person it looks most like (from 0.3, lower than auto
    /// matching, and never a person it was removed from). Only clear faces unless <paramref name="all"/>.
    /// </summary>
    public (List<FaceSuggestion> Faces, long Total) Unassigned(int offset, int limit, bool all = false)
    {
        var model = _faces.Pack.Name;
        var clear = all ? "" : " AND lf.score>=@minScore AND lf.eye_px>=@minEye";
        var p = new { model, minScore = _options.MinScore, minEye = _options.MinEyePx, limit = Math.Clamp(limit, 1, 500), offset = Math.Max(0, offset) };
        List<FaceRow> rows;
        long total;
        List<PassFace> assigned;
        Dictionary<long, string?> names;
        using (var c = _db.Open())
        {
            total = c.ExecuteScalar<long>($"SELECT COUNT(*) FROM live_faces lf WHERE lf.person_id IS NULL AND lf.model=@model{clear}", p);
            rows = c.Query<FaceRow>(FaceSelect + $" WHERE lf.person_id IS NULL AND lf.model=@model{clear} ORDER BY lf.eye_px * lf.score DESC, lf.id LIMIT @limit OFFSET @offset", p).ToList();
            assigned = c.Query<PassFace>("SELECT lf.id, lf.asset_id, lf.person_id, lf.assigned_by, lf.score, lf.eye_px, i.taken_at FROM live_faces lf JOIN items i ON i.id=lf.item_id WHERE lf.model=@model AND lf.person_id IS NOT NULL", p).ToList();
            names = c.Query<(long, string?)>("SELECT id, name FROM people").ToDictionary(x => x.Item1, x => x.Item2);
        }
        var infos = rows.Select(r => ToInfo(r, false)).ToList();
        var references = References(assigned);
        if (references.Count == 0 || infos.Count == 0) return (infos.Select(f => new FaceSuggestion(f, null, null, null)).ToList(), total);
        Dictionary<long, HashSet<long>> rejected;
        using (var c = _db.Open())
            rejected = c.Query<(long FaceId, long PersonId)>("SELECT face_id, person_id FROM face_rejections WHERE face_id IN @ids", new { ids = infos.Select(f => f.Id).ToList() })
                .GroupBy(r => r.FaceId).ToDictionary(g => g.Key, g => g.Select(r => r.PersonId).ToHashSet());
        var embeddings = LoadEmbeddings(references.Select(r => r.Id).Concat(infos.Select(f => f.Id)));
        var near = FaceGrouping.Nearest(infos.Select(f => embeddings[f.Id]).ToList(), references.Select(r => embeddings[r.Id]).ToList(), Neighbours, SuggestBelow);
        var result = new List<FaceSuggestion>(infos.Count);
        for (var i = 0; i < infos.Count; i++)
        {
            var no = rejected.GetValueOrDefault(infos[i].Id);
            var best = near[i].Select(x => (Person: references[x.Index].PersonId!.Value, x.Similarity))
                .Where(x => no is null || !no.Contains(x.Person))
                .GroupBy(x => x.Person).Select(g => (Person: g.Key, Votes: g.Count(), Best: g.Max(x => x.Similarity)))
                .OrderByDescending(x => x.Votes).ThenByDescending(x => x.Best).FirstOrDefault();
            result.Add(best.Votes > 0 ? new FaceSuggestion(infos[i], best.Person, names.GetValueOrDefault(best.Person), best.Best) : new FaceSuggestion(infos[i], null, null, null));
        }
        return (result, total);
    }

    // ---------------------------------------------------------------- corrections (admin)

    /// <summary>
    /// Assigns faces to a person, by id or by name (a new person when nobody has that name). Faces assigned by hand are
    /// never moved by a pass. Returns the person's id.
    /// </summary>
    public long Assign(IReadOnlyCollection<long> faceIds, long? personId, string? name)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        long id;
        if (personId is { } given)
        {
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM people WHERE id=@given", new { given }, tx) == 0) throw new KeyNotFoundException($"no person {given}");
            id = given;
        }
        else
        {
            var n = CleanName(name) ?? throw new ArgumentException("a person or a name is needed");
            id = c.ExecuteScalar<long?>("SELECT id FROM people WHERE name=@n", new { n }, tx)
                ?? c.ExecuteScalar<long>("INSERT INTO people(name, created_at, updated_at) VALUES(@n, @now, @now); SELECT last_insert_rowid();", new { n, now = Clock.NowIso() }, tx);
        }
        var before = c.Query<long>("SELECT DISTINCT person_id FROM faces WHERE id IN @faceIds AND person_id IS NOT NULL", new { faceIds }, tx).ToList();
        c.Execute("UPDATE faces SET person_id=@id, assigned_by='user' WHERE id IN @faceIds", new { id, faceIds }, tx);
        c.Execute("DELETE FROM face_rejections WHERE person_id=@id AND face_id IN @faceIds", new { id, faceIds }, tx);
        Touched(c, tx, before.Append(id));
        tx.Commit();
        _db.Touch();
        return id;
    }

    /// <summary>"Not this person": unassigns the faces, remembers they are not that person, and keeps passes away from them.</summary>
    public int Unassign(IReadOnlyCollection<long> faceIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var before = c.Query<long>("SELECT DISTINCT person_id FROM faces WHERE id IN @faceIds AND person_id IS NOT NULL", new { faceIds }, tx).ToList();
        c.Execute("INSERT OR IGNORE INTO face_rejections(face_id, person_id) SELECT id, person_id FROM faces WHERE id IN @faceIds AND person_id IS NOT NULL", new { faceIds }, tx);
        var n = c.Execute("UPDATE faces SET person_id=NULL, assigned_by='user' WHERE id IN @faceIds", new { faceIds }, tx);
        Touched(c, tx, before);
        tx.Commit();
        _db.Touch();
        return n;
    }

    /// <summary>Names (or renames) a person; the person tags follow. Fails when another person has the name: merge instead.</summary>
    public void Rename(long id, string? name)
    {
        var n = CleanName(name);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var old = c.QuerySingleOrDefault<string?>("SELECT name FROM people WHERE id=@id", new { id }, tx);
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM people WHERE id=@id", new { id }, tx) == 0) throw new KeyNotFoundException($"no person {id}");
        if (n is not null && c.ExecuteScalar<long?>("SELECT id FROM people WHERE name=@n AND id<>@id", new { n, id }, tx) is { } other)
            throw new PersonExistsException(n, other);
        c.Execute("UPDATE people SET name=@n, updated_at=@now WHERE id=@id", new { n, id, now = Clock.NowIso() }, tx);
        DropTags(c, tx, old);
        if (_options.PersonTags) SyncTags(c, tx, [id]);
        tx.Commit();
        _db.Touch();
    }

    public void SetHidden(long id, bool hidden)
    {
        using var c = _db.Open();
        if (c.Execute("UPDATE people SET hidden=@h, updated_at=@now WHERE id=@id", new { id, h = hidden ? 1 : 0, now = Clock.NowIso() }) == 0)
            throw new KeyNotFoundException($"no person {id}");
    }

    /// <summary>Chooses the face shown for a person; it must be one of theirs.</summary>
    public void SetCover(long id, long faceId)
    {
        using var c = _db.Open();
        if (c.Execute("UPDATE people SET cover_face_id=@faceId, updated_at=@now WHERE id=@id AND EXISTS (SELECT 1 FROM faces WHERE id=@faceId AND person_id=@id)",
                new { id, faceId, now = Clock.NowIso() }) == 0)
            throw new KeyNotFoundException($"face {faceId} is not one of person {id}'s");
    }

    /// <summary>Moves every face of one person to another and removes the first. A name is kept if only the first had one.</summary>
    public void Merge(long from, long into)
    {
        if (from == into) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var a = c.QuerySingleOrDefault<(long Id, string? Name)?>("SELECT id, name FROM people WHERE id=@from", new { from }, tx) ?? throw new KeyNotFoundException($"no person {from}");
        var b = c.QuerySingleOrDefault<(long Id, string? Name)?>("SELECT id, name FROM people WHERE id=@into", new { into }, tx) ?? throw new KeyNotFoundException($"no person {into}");
        c.Execute("UPDATE faces SET person_id=@into WHERE person_id=@from", new { from, into }, tx);
        c.Execute("UPDATE OR IGNORE face_rejections SET person_id=@into WHERE person_id=@from", new { from, into }, tx);
        c.Execute("UPDATE people SET cover_face_id=COALESCE(cover_face_id, (SELECT cover_face_id FROM people WHERE id=@from)), updated_at=@now WHERE id=@into", new { from, into, now = Clock.NowIso() }, tx);
        DropTags(c, tx, a.Name);
        c.Execute("DELETE FROM people WHERE id=@from", new { from }, tx);
        if (b.Name is null && a.Name is not null) c.Execute("UPDATE people SET name=@name WHERE id=@into", new { name = a.Name, into }, tx);
        if (_options.PersonTags) SyncTags(c, tx, [into]);
        tx.Commit();
        _db.Touch();
    }

    /// <summary>Removes a person. Their faces become unassigned and passes leave them alone, so a dismissed suggestion stays dismissed.</summary>
    public void Delete(long id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var name = c.QuerySingleOrDefault<string?>("SELECT name FROM people WHERE id=@id", new { id }, tx);
        c.Execute("UPDATE faces SET person_id=NULL, assigned_by='user' WHERE person_id=@id", new { id }, tx);
        DropTags(c, tx, name);
        if (c.Execute("DELETE FROM people WHERE id=@id", new { id }, tx) == 0) throw new KeyNotFoundException($"no person {id}");
        tx.Commit();
        _db.Touch();
    }

    private void Touched(SqliteConnection c, IDbTransaction tx, IEnumerable<long> personIds)
    {
        Tidy(c, tx);
        if (_options.PersonTags) SyncTags(c, tx, personIds.Distinct().ToList());
    }

    private static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

/// <summary>Naming a person failed because another person has the name. Merge the two instead.</summary>
public sealed class PersonExistsException(string name, long existingId) : InvalidOperationException($"There is already a person called {name}")
{
    public long ExistingId { get; } = existingId;
}
