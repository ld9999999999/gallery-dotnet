using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Settings that can change while the app runs, stored in the settings table.</summary>
public sealed class SettingsService
{
    public const string DerivedDir = "derived_dir";
    public const string KeepTranscodes = "keep_transcodes";
    /// <summary>While the server is idle, convert videos that browsers can't play (<see cref="IdleTranscoder"/>).</summary>
    public const string TranscodeWhenIdle = "transcode_when_idle";
    public const string ViewerPasswordRequired = "viewer_password_required";
    public const string GalleryTitle = "gallery_title";
    public const string ThumbQuality = "thumb_quality";
    public const string PregenThumbs = "pregen_thumbs";
    /// <summary>Set from Admin → Faces. Until it is set, Gallery:Faces:Enabled and Gallery:Faces:Pack are used.</summary>
    public const string FacesEnabled = "faces_enabled";
    public const string FacesPack = "faces_pack";

    private readonly Db _db;
    private readonly GalleryOptions _opt;

    public SettingsService(Db db, GalleryOptions opt)
    {
        _db = db;
        _opt = opt;
    }

    public string DataDir => _opt.ResolvedDataDir;

    public string Get(string key, string fallback = "")
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key }) ?? fallback;
    }

    public bool GetBool(string key, bool fallback = false)
    {
        var v = Get(key, fallback ? "true" : "false");
        return v is "1" or "true" or "True";
    }

    public void Set(string key, string value)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO settings(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { key, value });
    }

    public Dictionary<string, string> All()
    {
        using var c = _db.Open();
        return c.Query<(string key, string value)>("SELECT key, value FROM settings").ToDictionary(x => x.key, x => x.value);
    }

    /// <summary>Directory for generated content (transcodes, edits). Defaults to {DataDir}/derived.</summary>
    public string GetDerivedDir()
    {
        var v = Get(DerivedDir);
        var dir = string.IsNullOrWhiteSpace(v) ? Path.Combine(DataDir, "derived") : GalleryOptions.ExpandHome(v);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ThumbsDir
    {
        get
        {
            var d = Path.Combine(DataDir, "thumbs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public void SeedDefaults()
    {
        var all = All();
        void Default(string k, string v) { if (!all.ContainsKey(k)) Set(k, v); }
        Default(KeepTranscodes, "true");
        Default(TranscodeWhenIdle, "false");
        Default(ViewerPasswordRequired, "false");
        Default(GalleryTitle, "Gallery");
        Default(ThumbQuality, "82");
        Default(PregenThumbs, "true");
    }
}
