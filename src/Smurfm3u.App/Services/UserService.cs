using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Local accounts for the web UI. The external APIs authenticate with the shared API key
/// instead, because that is what Prowlarr, Sonarr and Radarr know how to send.
/// </summary>
public class UserService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public async Task<AppUser?> ValidateAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (user is null)
        {
            // Hash anyway so a missing user and a wrong password take the same time.
            _ = Verify(password, HashPassword(password));
            return null;
        }

        if (!Verify(password, user.PasswordHash))
            return null;

        user.LastLoginAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return user;
    }

    public async Task<bool> ChangePasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (user is null || !Verify(currentPassword, user.PasswordHash))
            return false;

        user.PasswordHash = HashPassword(newPassword);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AnyUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(ct);
    }

    public async Task CreateAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.Users.Add(new AppUser
        {
            Username = username,
            PasswordHash = HashPassword(password),
            CreatedAt = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>PBKDF2-SHA256, stored as "v1.iterations.salt.hash" with both parts base64.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, Algorithm, KeySize);

        return string.Join('.', "v1", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, Algorithm, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
