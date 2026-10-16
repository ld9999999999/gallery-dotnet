namespace Gallery.Core.Models;

/// <summary>Settings read from appsettings.json / environment. Settings the admin can change while running live in the DB (see SettingsService).</summary>
public sealed class GalleryOptions
{
    public const string Section = "Gallery";

    /// <summary>Folder for the SQLite database and everything we generate (thumbnails, transcodes, edits).</summary>
    public string DataDir { get; set; } = "~/.gallery";

    /// <summary>Source folders added to the DB on first start. The admin can add more in the UI.</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Password for the first admin account. If empty, a random one is made and written to DataDir/admin-initial-password.txt.</summary>
    public string InitialAdminPassword { get; set; } = "";

    /// <summary>Seconds between console progress lines while scanning / generating thumbnails / transcoding. 0 = off.</summary>
    public int ProgressLogSeconds { get; set; } = 5;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>LibRaw's tools, for editing RAW files (Debian/Ubuntu: libraw-bin; macOS: brew install libraw).</summary>
    public string DcrawEmuPath { get; set; } = "dcraw_emu";
    public string RawIdentifyPath { get; set; } = "raw-identify";

    /// <summary>libheif's tools, for HEIC photos (Debian/Ubuntu: libheif-examples; macOS: brew install libheif). Without them, ffmpeg is used.</summary>
    public string HeifDecPath { get; set; } = "heif-dec";
    public string HeifInfoPath { get; set; } = "heif-info";

    /// <summary>Space for cached editing copies under {derived}/develop. Above it, the least recently used are removed.</summary>
    public int DevelopCacheMB { get; set; } = 4096;

    /// <summary>Certificate for the https:// addresses in Urls (Gallery:Https:*).</summary>
    public HttpsOptions Https { get; set; } = new();

    /// <summary>Finding faces (Gallery:Faces:*); see docs/faces.md.</summary>
    public FaceOptions Faces { get; set; } = new();

    public string ResolvedDataDir => ExpandHome(DataDir);

    public static string ExpandHome(string path)
    {
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
        }
        return Path.GetFullPath(path);
    }
}

/// <summary>
/// Leave CertificatePath empty to have a self-signed certificate created in DataDir/https (see HttpsCertificates).
/// </summary>
public sealed class HttpsOptions
{
    /// <summary>A PEM certificate (a full-chain file may add the middle certificates after it) or a .pfx/.p12 file.</summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>The PEM private key, when it isn't in the CertificatePath file. Ignored for .pfx.</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>Password of an encrypted PEM key or a .pfx.</summary>
    public string Password { get; set; } = "";

    /// <summary>Extra host names or IP addresses the generated certificate must cover, e.g. a DNS name for this server.</summary>
    public List<string> Hosts { get; set; } = new();

    /// <summary>Send http:// requests to the https:// address.</summary>
    public bool RedirectHttp { get; set; }
}

/// <summary>
/// Finding faces. Enabled and Pack are defaults: once the admin changes them (Admin → Faces), the database values win.
/// The rest come only from configuration.
/// </summary>
public sealed class FaceOptions
{
    /// <summary>Find faces when the pack's model files are present.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>buffalo_l (accurate) or buffalo_s (for low-power CPUs).</summary>
    public string Pack { get; set; } = "buffalo_l";

    /// <summary>auto, cpu, coreml, cuda or openvino. If any of them fails to start or run, the CPU is used instead.</summary>
    public string Provider { get; set; } = "auto";

    /// <summary>Long edge of the picture given to the face detector.</summary>
    public int DetectorSize { get; set; } = 640;

    /// <summary>Worker threads; 0 means half the logical cores.</summary>
    public int Workers { get; set; }

    /// <summary>Folder holding one subfolder per pack. Empty: {DataDir}/models/faces.</summary>
    public string ModelsDir { get; set; } = "";

    /// <summary>Cosine similarity a new face needs with a person's faces to join that person.</summary>
    public double MatchThreshold { get; set; } = 0.5;

    /// <summary>Average similarity unassigned faces need to be grouped into a suggested person.</summary>
    public double GroupThreshold { get; set; } = 0.45;

    /// <summary>How many faces a group needs before it is suggested as a person.</summary>
    public int MinGroupSize { get; set; } = 3;

    /// <summary>Detector score a face needs to be used in matching and grouping (faces from 0.5 up are stored).</summary>
    public double MinScore { get; set; } = 0.7;

    /// <summary>Distance between the eyes, in the picture fitted to 1024 px, that a face needs to be used in matching and grouping.</summary>
    public double MinEyePx { get; set; } = 12;

    /// <summary>Give the items of a named person the tag "person: Name".</summary>
    public bool PersonTags { get; set; } = true;
}
