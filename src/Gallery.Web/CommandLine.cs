using Gallery.Core.Models;

namespace Gallery.Web;

/// <summary>
/// Cleans up the command line before .NET's configuration parser reads it:
/// <list type="bullet">
/// <item><c>--help</c> (or <c>-h</c>, <c>-?</c>) asks for <see cref="Usage"/> instead of starting the server.</item>
/// <item><c>--verbose</c> is our own flag and takes no value; it turns on debug logging.</item>
/// <item>A bare <c>--</c> is the separator <c>dotnet run</c> needs before app arguments. <c>dotnet run</c> removes it,
/// but when the built program is started directly it arrives as an argument. The parser reads it as an empty key that
/// takes the next argument as its value, so e.g. <c>-- --Gallery:DataDir=/x</c> silently loses the data folder. We
/// drop it so both ways of starting the app behave the same.</item>
/// </list>
/// </summary>
public static class CommandLine
{
    public sealed record Parsed(string[] Args, bool Verbose, int SeparatorsDropped, bool Help = false);

    private static readonly string[] HelpFlags = ["--help", "-h", "-?", "/?"];

    public static Parsed Normalize(string[] args)
    {
        var verbose = args.Contains("--verbose");
        var help = args.Any(a => HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase));
        var separators = args.Count(a => a == "--");
        var rest = args.Where(a => a != "--verbose" && a != "--" && !HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        return new Parsed(rest, verbose, separators, help);
    }

    /// <summary>The addresses in appsettings.json's Urls.</summary>
    public const string DefaultUrls = "http://0.0.0.0:5080;https://0.0.0.0:5443";

    /// <summary>What <c>--help</c> prints: every option, with its default.</summary>
    public static string Usage()
    {
        var d = new GalleryOptions();
        return $"""
            Gallery web server: browse a folder tree of photos, videos and music in the browser, without moving
            or changing the files.

            Usage:
              Gallery.Web [options]
              dotnet run --project src/Gallery.Web -- [options]

            Options:
              -h, --help                          Show this help and exit.
              --verbose                           Debug logging: every file indexed and every thumbnail made.
              --Urls=<urls>                       Addresses to listen on, separated by ';'. Leave out the https://
                                                  address to serve plain HTTP only.
                                                  Default: {DefaultUrls}

            Library:
              --Gallery:DataDir=<dir>             Database, thumbnails, transcodes and edits. Default: {d.DataDir}
              --Gallery:Sources:<n>=<dir>         A folder of photos, videos or music to index; n = 0, 1, 2...
                                                  Added on first start; later, Admin > Sources & scan.
              --Gallery:InitialAdminPassword=<p>  Password of the admin account when it is created. Empty: a random
                                                  one is logged and written to <DataDir>/admin-initial-password.txt.
              --Gallery:ProgressLogSeconds=<s>    Seconds between progress lines while scanning; 0 for none.
                                                  Default: {d.ProgressLogSeconds}

            Tools:
              --Gallery:FfmpegPath=<path>         ffmpeg, for video and audio. Default: {d.FfmpegPath}
              --Gallery:FfprobePath=<path>        ffprobe. Default: {d.FfprobePath}
              --Gallery:DcrawEmuPath=<path>       LibRaw's dcraw_emu, for editing RAW files. Default: {d.DcrawEmuPath}
              --Gallery:RawIdentifyPath=<path>    LibRaw's raw-identify. Default: {d.RawIdentifyPath}
              --Gallery:HeifDecPath=<path>        libheif's heif-dec, for HEIC photos (ffmpeg is the fallback).
                                                  Default: {d.HeifDecPath}
              --Gallery:HeifInfoPath=<path>       libheif's heif-info, for their size. Default: {d.HeifInfoPath}
              --Gallery:DevelopCacheMB=<mb>       Space for the photo editor's cached copies. Default: {d.DevelopCacheMB}

            Faces (model files: Admin > Faces downloads them):
              --Gallery:Faces:Enabled=<bool>      Find faces in photos when the model files are present; Admin > Faces
                                                  overrides it once changed there. Default: {d.Faces.Enabled.ToString().ToLowerInvariant()}
              --Gallery:Faces:Pack=<name>         buffalo_l (accurate) or buffalo_s (low-power CPUs). Default: {d.Faces.Pack}
              --Gallery:Faces:Provider=<name>     auto, cpu, coreml, cuda or openvino; falls back to the CPU.
                                                  Default: {d.Faces.Provider}
              --Gallery:Faces:DetectorSize=<px>   Long edge given to the face detector. Default: {d.Faces.DetectorSize}
              --Gallery:Faces:Workers=<n>         Worker threads; 0 for half the cores. Default: {d.Faces.Workers}
              --Gallery:Faces:ModelsDir=<dir>     Where the packs live. Default: <DataDir>/models/faces
              --Gallery:Faces:MatchThreshold=<s>  Similarity a new face needs to join a person. Default: {d.Faces.MatchThreshold}
              --Gallery:Faces:GroupThreshold=<s>  Average similarity to group faces into a suggested person.
                                                  Default: {d.Faces.GroupThreshold}
              --Gallery:Faces:MinGroupSize=<n>    Faces in a group before it is suggested. Default: {d.Faces.MinGroupSize}
              --Gallery:Faces:MinScore=<s>        Detector score a face needs to be grouped. Default: {d.Faces.MinScore}
              --Gallery:Faces:MinEyePx=<px>       Eye distance (in the 1024 px picture) a face needs to be grouped.
                                                  Default: {d.Faces.MinEyePx}
              --Gallery:Faces:PersonTags=<bool>   Tag a named person's items "person: Name". Default: {d.Faces.PersonTags.ToString().ToLowerInvariant()}

            HTTPS (without a certificate, a self-signed one is made in <DataDir>/https):
              --Gallery:Https:CertificatePath=<file>  A PEM certificate (a full chain may follow it) or a .pfx/.p12.
              --Gallery:Https:KeyPath=<file>          The PEM private key, when it is not in the certificate file.
              --Gallery:Https:Password=<p>            Password of an encrypted key or a .pfx.
              --Gallery:Https:Hosts:<n>=<name>        More names or addresses the made certificate covers; n = 0, 1...
              --Gallery:Https:RedirectHttp=true       Send http:// requests to the https:// address.

            An option can also be given as "--Key value", put in appsettings.json, or set in the environment
            with "__" for ":", e.g. Gallery__DataDir=/srv/gallery or Gallery__Sources__0=/photos.

            Example:
              Gallery.Web --Gallery:DataDir=/srv/gallery --Gallery:Sources:0=/photos --Urls=http://0.0.0.0:8080

            """;
    }
}
