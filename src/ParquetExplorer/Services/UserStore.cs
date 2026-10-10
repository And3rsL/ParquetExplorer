using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

public enum LoginResult
{
    Success,
    InvalidCredentials,
    LockedOut,
}

/// <summary>Validates users configured in appsettings (PBKDF2 hashed passwords) with brute-force lockout.</summary>
public sealed class UserStore(IOptionsMonitor<ExplorerOptions> options, ILogger<UserStore> logger)
{
    private static readonly PasswordHasher<string> Hasher = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset? LockedUntil)> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public static string HashPassword(string password) => Hasher.HashPassword("", password);

    public (LoginResult Result, ClaimsPrincipal? Principal) ValidateCredentials(string username, string password)
    {
        var auth = options.CurrentValue.Auth;
        username = username.Trim();

        if (_attempts.TryGetValue(username, out var state) && state.LockedUntil > DateTimeOffset.UtcNow)
            return (LoginResult.LockedOut, null);

        var user = auth.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        // Always run the hash verification so timing does not reveal whether the user exists.
        var hash = user?.PasswordHash is { Length: > 0 } h ? h : DummyHash.Value;
        var verified = false;
        try
        {
            verified = Hasher.VerifyHashedPassword("", hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            logger.LogError("Invalid password hash configured for user {User}", username);
        }

        if (user is null || !verified)
        {
            var failures = (state.Failures) + 1;
            DateTimeOffset? lockedUntil = failures >= auth.MaxFailedAttempts
                ? DateTimeOffset.UtcNow.AddMinutes(auth.LockoutMinutes)
                : null;
            _attempts[username] = (lockedUntil is null ? failures : 0, lockedUntil);
            logger.LogWarning("Failed login for {User}{Locked}", username, lockedUntil is null ? "" : " (locked out)");
            return (lockedUntil is null ? LoginResult.InvalidCredentials : LoginResult.LockedOut, null);
        }

        _attempts.TryRemove(username, out _);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new("display_name", user.DisplayName ?? user.Username),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        logger.LogInformation("User {User} signed in", user.Username);
        return (LoginResult.Success, new ClaimsPrincipal(identity));
    }

    /// <summary>Re-checks that a signed-in user still exists in configuration.</summary>
    public bool Exists(string? username) =>
        username is not null && options.CurrentValue.Auth.Users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword("", Guid.NewGuid().ToString()));
}
