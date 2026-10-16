using Gallery.Core;
using Gallery.Web;
using Gallery.Web.Endpoints;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

// --help prints the options and exits. --verbose (debug logging) and a bare "--" (left over when the built program is
// run directly) are removed before the configuration parser sees them. See CommandLine.
var commandLine = CommandLine.Normalize(args);
if (commandLine.Help)
{
    // Handle this before any setup, so asking for help makes no data folder, certificate or log noise.
    Console.Out.Write(CommandLine.Usage());
    return;
}
var verbose = commandLine.Verbose;
args = commandLine.Args;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
if (verbose) builder.Logging.AddFilter("Gallery", LogLevel.Debug);

var options = builder.Configuration.GetSection(GalleryOptions.Section).Get<GalleryOptions>() ?? new GalleryOptions();
var dataDir = options.ResolvedDataDir;
builder.Services.AddGalleryCore(options);
// Idle video conversion is for browsers, so only the web server runs it (it also knows when the gallery is in use).
builder.Services.AddSingleton<ActivityMonitor>();
builder.Services.AddSingleton<IdleTranscoder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleTranscoder>());
// The same for freeing memory when the gallery is quiet; otherwise an idle server keeps what its last busy time used.
builder.Services.AddSingleton<IdleMemoryRelease>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleMemoryRelease>());
builder.Services.AddSingleton<UserService>();
HttpsCertificates.Configure(builder);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "gallery.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        // API: answer 401/403 rather than redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.RequireRole(UserService.AdminRole));
    // Viewer: any signed-in user, or an anonymous visitor if the admin has not set a viewer password.
    o.AddPolicy("Viewer", p => p.RequireAssertion(ctx =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true) return true;
        var http = ctx.Resource as HttpContext;
        var settings = http?.RequestServices.GetRequiredService<SettingsService>();
        return settings is not null && !settings.GetBool(SettingsService.ViewerPasswordRequired);
    }));
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// --- startup: schema, defaults, seed sources, admin user ---
GalleryCore.Prepare(app.Services, app.Logger);
var generated = app.Services.GetRequiredService<UserService>().EnsureAdmin(options.InitialAdminPassword, dataDir);
if (generated is not null)
    app.Logger.LogWarning("=== Initial admin password: {Password} ===", generated);

// Every request except a page's own status polls means someone is using the gallery. Idle-only work waits for quiet.
var activity = app.Services.GetRequiredService<ActivityMonitor>();
app.Use((ctx, next) => { if (!ActivityMonitor.IsPoll(ctx.Request.Path.Value)) activity.Seen(); return next(ctx); });
if (options.Https.RedirectHttp) app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The editor's WebAssembly files have a content hash in their names (Gallery.Develop.Wasm.x2uh9l6wor.wasm) and never
    // change, so cache them forever. Check everything else (index.html, app.js, editor/_framework/dotnet.js…) on each
    // load, so an updated gallery never ends up mixed with an old cached script.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        Fingerprinted().IsMatch(ctx.File.Name) ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();

AuthEndpoints.Map(app);
ItemEndpoints.Map(app);
AssetEndpoints.Map(app);
CollageEndpoints.Map(app);
AlbumEndpoints.Map(app);
AdminEndpoints.Map(app);
DevelopEndpoints.Map(app);
DevelopEndpoints.MapExports(app);
RecipeEndpoints.Map(app);
PeopleEndpoints.Map(app);

app.MapGet("/api/health", () => Results.Ok(new { ok = true })).AllowAnonymous();
app.MapFallbackToFile("index.html");

GalleryCore.QueueStartupScan(app.Services, app.Logger);
if (verbose) app.Logger.LogInformation("Verbose logging on: every indexed file and thumbnail is logged");
if (commandLine.SeparatorsDropped > 0)
    app.Logger.LogInformation("Ignored a bare '--' on the command line; it is only needed with 'dotnet run'");

app.Run();

static partial class Program
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\.[a-z0-9]{10}\.(wasm|js|dat|json)(\.br|\.gz)?$")]
    private static partial System.Text.RegularExpressions.Regex Fingerprinted();
}
