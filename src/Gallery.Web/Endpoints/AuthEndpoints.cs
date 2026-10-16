using System.Security.Claims;
using Gallery.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Gallery.Web.Endpoints;

public static class AuthEndpoints
{
    public record LoginRequest(string Username, string? Password);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, HttpContext ctx, UserService users) =>
        {
            var u = users.Authenticate(req.Username?.Trim() ?? "", req.Password ?? "");
            if (u is null) return Results.Unauthorized();
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, u.Username),
                new Claim(ClaimTypes.Role, u.Role),
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            return Results.Ok(new { username = u.Username, role = u.Role });
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync();
            return Results.Ok();
        }).AllowAnonymous();

        g.MapGet("/me", (HttpContext ctx, SettingsService settings) =>
        {
            var authenticated = ctx.User.Identity?.IsAuthenticated == true;
            var role = ctx.User.FindFirstValue(ClaimTypes.Role);
            var viewerPasswordRequired = settings.GetBool(SettingsService.ViewerPasswordRequired);
            return Results.Ok(new
            {
                authenticated,
                username = authenticated ? ctx.User.Identity!.Name : null,
                role = role ?? (viewerPasswordRequired ? null : UserService.ViewerRole),
                viewerPasswordRequired,
                title = settings.Get(SettingsService.GalleryTitle, "Gallery"),
            });
        }).AllowAnonymous();
    }
}
