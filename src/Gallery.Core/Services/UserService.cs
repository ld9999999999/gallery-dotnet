using System.Security.Cryptography;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed class UserService
{
    public const string AdminRole = "admin";
    public const string ViewerRole = "viewer";
    public const string ViewerUsername = "viewer";

    private readonly Db _db;
    private readonly ILogger<UserService> _log;

    public UserService(Db db, ILogger<UserService> log)
    {
        _db = db;
        _log = log;
    }

    public User? Find(string username)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<User>("SELECT * FROM users WHERE username=@username", new { username });
    }

    public IEnumerable<User> All()
    {
        using var c = _db.Open();
        return c.Query<User>("SELECT id, username, role, created_at, CASE WHEN password_hash IS NULL THEN NULL ELSE '***' END AS password_hash FROM users ORDER BY role, username");
    }

    public User? Authenticate(string username, string password)
    {
        var u = Find(username);
        if (u is null) return null;
        if (u.PasswordHash is null) return string.IsNullOrEmpty(password) ? u : null;
        return Verify(password, u.PasswordHash) ? u : null;
    }

    public void SetPassword(string username, string? password, string role)
    {
        using var c = _db.Open();
        var hash = string.IsNullOrEmpty(password) ? null : Hash(password);
        c.Execute("""
            INSERT INTO users(username, password_hash, role, created_at) VALUES(@username, @hash, @role, @now)
            ON CONFLICT(username) DO UPDATE SET password_hash=excluded.password_hash
            """, new { username, hash, role, now = Clock.NowIso() });
    }

    /// <summary>Creates the admin account on first start. Returns the generated password when one was created.</summary>
    public string? EnsureAdmin(string configuredPassword, string dataDir)
    {
        using var c = _db.Open();
        var exists = c.ExecuteScalar<long>("SELECT COUNT(*) FROM users WHERE role='admin'") > 0;
        if (exists) return null;

        var pw = string.IsNullOrWhiteSpace(configuredPassword)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y')
            : configuredPassword;
        SetPassword("admin", pw, AdminRole);
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            var file = Path.Combine(dataDir, "admin-initial-password.txt");
            File.WriteAllText(file, pw + Environment.NewLine);
            _log.LogWarning("Created admin user. Initial password written to {File}", file);
            return pw;
        }
        _log.LogInformation("Created admin user from configured password.");
        return null;
    }

    // PBKDF2-SHA256, stored as: pbkdf2$iterations$saltB64$hashB64
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iters = 100_000;
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${iters}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        var iters = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
