using Dapper;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Data;

/// <summary>Opens SQLite connections and applies the schema. One file: {DataDir}/gallery.db.</summary>
public sealed class Db
{
    private readonly string _connectionString;
    private long _version;

    public string DbPath { get; }

    public Db(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "gallery.db");
        // Give each connection its own cache. A shared cache makes SQLite lock whole tables between connections:
        // reading assets while the scanner or file watcher writes them fails with "database table is locked", and
        // waiting (busy_timeout) doesn't help. With private caches, WAL lets readers keep using the last
        // committed data while one connection writes.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return c;
    }

    /// <summary>Change counter for library content (assets/items). The scanner increases it; used to cache stats.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Touch() => Interlocked.Increment(ref _version);

    public void Migrate()
    {
        using var c = Open();
        c.Execute(Schema);
        // Columns that older databases may lack: CREATE TABLE IF NOT EXISTS does not add them to existing tables.
        EnsureColumn(c, "assets", "taken_source", "TEXT");
        EnsureColumn(c, "assets", "edit_of", "INTEGER");          // an exported edit: the asset it was made from
        EnsureColumn(c, "items", "preferred_asset_id", "INTEGER"); // the main file someone chose; kept across rebuilds
        // Hidden from viewers by an admin (admins still see it). Hiding an album this way hides everything in it.
        EnsureColumn(c, "items", "viewer_hidden", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(c, "albums", "viewer_hidden", "INTEGER NOT NULL DEFAULT 0");
        // 'faces' marks the person tags written by the face service, so person tags added by hand are left alone.
        EnsureColumn(c, "item_tags", "source", "TEXT");
        // 'equirectangular' for a 360° photo, NULL for a flat one (or one not checked yet; see ProjectionBackfill).
        EnsureColumn(c, "assets", "projection", "TEXT");
        EnsureColumn(c, "face_scans", "squeeze", "REAL NOT NULL DEFAULT 1"); // the anamorphic squeeze the faces were found at
        // Made again on every start, so it follows changes to the faces table.
        c.Execute("DROP VIEW IF EXISTS live_faces; " + LiveFacesView);
    }

    /// <summary>
    /// The faces that count: those on the file each item is searched by (its main file, or for an exported edit the file it
    /// was made from), in items that still have files, with their item. Faces of a file that is no longer an item's main
    /// file stay stored but don't count. Read starting from the faces table, so every join uses a key.
    /// </summary>
    private const string LiveFacesView = """
        CREATE VIEW live_faces AS
        SELECT f.*, i.id AS item_id
        FROM faces f
        JOIN assets a ON a.id=f.asset_id
        JOIN items i ON i.id=a.item_id
        JOIN assets p ON p.id=i.primary_asset_id
        WHERE i.hidden=0 AND i.kind='image'
          AND ((a.id=p.id AND (p.edit_of IS NULL OR NOT EXISTS (SELECT 1 FROM assets s WHERE s.id=p.edit_of AND s.missing=0)))
               OR (a.id=p.edit_of AND a.missing=0));
        """;

    private static void EnsureColumn(SqliteConnection c, string table, string column, string type)
    {
        var columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')");
        if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            id            INTEGER PRIMARY KEY,
            username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT,
            role          TEXT NOT NULL,
            created_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sources (
            id       INTEGER PRIMARY KEY,
            path     TEXT NOT NULL UNIQUE,
            enabled  INTEGER NOT NULL DEFAULT 1,
            added_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS items (
            id               INTEGER PRIMARY KEY,
            group_key        TEXT NOT NULL UNIQUE,
            kind             TEXT NOT NULL,
            taken_at         TEXT,
            taken_year       INTEGER,
            title            TEXT NOT NULL,
            dir_name         TEXT NOT NULL,
            primary_asset_id INTEGER,
            width            INTEGER,
            height           INTEGER,
            duration_sec     REAL,
            asset_count      INTEGER NOT NULL DEFAULT 0,
            hidden           INTEGER NOT NULL DEFAULT 0,
            created_at       TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_items_taken ON items(taken_at);
        CREATE INDEX IF NOT EXISTS ix_items_year  ON items(taken_year);
        CREATE INDEX IF NOT EXISTS ix_items_kind  ON items(kind);
        CREATE INDEX IF NOT EXISTS ix_items_title ON items(title COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_items_dir   ON items(dir_name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS assets (
            id             INTEGER PRIMARY KEY,
            item_id        INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            path           TEXT NOT NULL UNIQUE,
            file_name      TEXT NOT NULL,
            ext            TEXT NOT NULL,
            size           INTEGER NOT NULL,
            mtime          INTEGER NOT NULL,
            hash           TEXT NOT NULL,
            kind           TEXT NOT NULL,
            is_raw         INTEGER NOT NULL DEFAULT 0,
            width          INTEGER,
            height         INTEGER,
            duration_sec   REAL,
            taken_at       TEXT,
            taken_source   TEXT,             -- metadata | filename | file | none (NULL: indexed before this was recorded)
            codec          TEXT,
            audio_codec    TEXT,
            container      TEXT,
            last_seen_scan INTEGER NOT NULL DEFAULT 0,
            missing        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_assets_item ON assets(item_id);
        CREATE INDEX IF NOT EXISTS ix_assets_hash ON assets(hash);
        CREATE INDEX IF NOT EXISTS ix_assets_name ON assets(file_name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_assets_taken ON assets(taken_at);

        CREATE TABLE IF NOT EXISTS item_tags (
            item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            key     TEXT NOT NULL COLLATE NOCASE,
            value   TEXT NOT NULL,
            PRIMARY KEY (item_id, key, value)
        );
        CREATE INDEX IF NOT EXISTS ix_tags_kv ON item_tags(key, value);

        CREATE TABLE IF NOT EXISTS albums (
            id            INTEGER PRIMARY KEY,
            name          TEXT NOT NULL,
            kind          TEXT NOT NULL DEFAULT 'manual',
            auto_dir_name TEXT,
            date_from     TEXT,
            date_to       TEXT,
            cover_item_id INTEGER,
            created_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS album_items (
            album_id INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            item_id  INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            position INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (album_id, item_id)
        );
        CREATE INDEX IF NOT EXISTS ix_album_items_item ON album_items(item_id);

        CREATE TABLE IF NOT EXISTS scan_runs (
            id            INTEGER PRIMARY KEY,
            started_at    TEXT NOT NULL,
            finished_at   TEXT,
            files_seen    INTEGER NOT NULL DEFAULT 0,
            files_added   INTEGER NOT NULL DEFAULT 0,
            files_updated INTEGER NOT NULL DEFAULT 0,
            files_missing INTEGER NOT NULL DEFAULT 0,
            status        TEXT NOT NULL,
            message       TEXT
        );

        CREATE TABLE IF NOT EXISTS derived (
            id         INTEGER PRIMARY KEY,
            asset_id   INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            kind       TEXT NOT NULL,          -- transcode | edit
            path       TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_derived_asset ON derived(asset_id, kind);

        -- Named sets of editor settings (sections: which groups apply), for applying one look to many photos.
        CREATE TABLE IF NOT EXISTS recipes (
            id         INTEGER PRIMARY KEY,
            name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
            params     TEXT NOT NULL,
            sections   TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        -- Photo editor settings (Gallery.Develop JSON), one edit per file. previous_params lets one recipe apply be undone.
        CREATE TABLE IF NOT EXISTS edits (
            asset_id        INTEGER PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            params          TEXT NOT NULL,
            previous_params TEXT,
            recipe_id       INTEGER,
            updated_at      TEXT NOT NULL
        );

        -- Faces (docs/faces.md). Keyed by file, like edits, so rebuilds that regroup files into new items keep them.
        -- One row per file searched, even with no faces: the queue resumes from these, and redoes a file when the pack
        -- or the item's rotation changes.
        CREATE TABLE IF NOT EXISTS face_scans (
            asset_id   INTEGER PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            model      TEXT NOT NULL,             -- the pack: buffalo_l | buffalo_s
            rotation   INTEGER NOT NULL DEFAULT 0, -- the item's rotate-degrees when searched
            face_count INTEGER NOT NULL DEFAULT 0,
            status     TEXT NOT NULL,             -- done | failed
            error      TEXT,
            scanned_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS people (
            id            INTEGER PRIMARY KEY,
            name          TEXT UNIQUE COLLATE NOCASE, -- NULL: a suggestion from grouping, not named yet
            cover_face_id INTEGER,
            hidden        INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS faces (
            id          INTEGER PRIMARY KEY,
            asset_id    INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            model       TEXT NOT NULL,
            x1 REAL NOT NULL, y1 REAL NOT NULL, x2 REAL NOT NULL, y2 REAL NOT NULL, -- box, fractions of the upright picture
            landmarks   BLOB NOT NULL,             -- 10 float32: eyes, nose tip, mouth corners, fractions likewise
            score       REAL NOT NULL,
            eye_px      REAL NOT NULL,             -- eye distance in the picture fitted to 1024 px
            embedding   BLOB NOT NULL,             -- the fingerprint: 512 float16, length 1
            person_id   INTEGER REFERENCES people(id) ON DELETE SET NULL,
            assigned_by TEXT,                      -- auto | user; NULL when nobody is assigned
            created_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_faces_asset ON faces(asset_id);
        CREATE INDEX IF NOT EXISTS ix_faces_person ON faces(person_id);

        -- "Not this person": automatic passes never put the face back there.
        CREATE TABLE IF NOT EXISTS face_rejections (
            face_id   INTEGER NOT NULL REFERENCES faces(id) ON DELETE CASCADE,
            person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
            PRIMARY KEY (face_id, person_id)
        );

        -- The photo an admin chose to show for a year in the Years view (else its newest).
        CREATE TABLE IF NOT EXISTS year_covers (
            year    INTEGER PRIMARY KEY,
            item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE
        );
        """;
}

public static class Clock
{
    public static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
