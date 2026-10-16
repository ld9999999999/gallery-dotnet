using Gallery.Core.Services;

namespace Gallery.Web.Endpoints;

public static class Access
{
    /// <summary>
    /// Whether the request comes from a viewer, meaning anyone not signed in as an admin. Viewers don't see items or albums
    /// an admin has hidden; admins see everything.
    /// </summary>
    public static bool IsViewer(this HttpContext ctx) => !ctx.User.IsInRole(UserService.AdminRole);
}
