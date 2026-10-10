using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
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

/// <summary>A user as shown in the admin page.</summary>
public sealed record UserSummary(
    string Username,
    string DisplayName,
    bool IsAdmin,
    bool Disabled,
    bool IsBuiltIn,
    DateTimeOffset? CreatedAt,
    string? CreatedBy);

/// <summary>
/// Users: the built-in ones from configuration (the initial admin, not editable from the UI) plus
/// the ones managed from the admin page, persisted to <c>users.json</c> with PBKDF2 password hashes.
/// </summary>
public sealed partial class UserStore
{
    public const int MinPasswordLength = 8;

    private sealed class ManagedUser
    {
        public string Username { get; set; } = "";
        public string? DisplayName { get; set; }
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = Roles.Viewer;
        public bool Disabled { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    private sealed class Document
    {
        public List<ManagedUser> Users { get; set; } = [];
    }

    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword("", Guid.NewGuid().ToString()));

    private readonly IOptionsMonitor<ExplorerOptions> _options;
    private readonly ILogger<UserStore> _logger;
    private readonly JsonFileStore<Document> _store;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset? LockedUntil)> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private List<ManagedUser> _managed;

    public event Action? Changed;

    public UserStore(IOptionsMonitor<ExplorerOptions> options, ILogger<UserStore> logger)
    {
        _options = options;
        _logger = logger;
        _store = new JsonFileStore<Document>(Path.Combine(options.CurrentValue.StateDirectory, "users.json"));
        try
        {
            _managed = _store.Load().Users;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot read {File}", _store.Path);
            _managed = [];
        }
    }

    public static string HashPassword(string password) => Hasher.HashPassword("", password);

    private IEnumerable<ParquetExplorer.Options.UserOptions> BuiltIn => _options.CurrentValue.Auth.Users;

    // ---------------- Authentication ----------------

    public (LoginResult Result, ClaimsPrincipal? Principal) ValidateCredentials(string username, string password)
    {
        var auth = _options.CurrentValue.Auth;
        username = username.Trim();

        if (_attempts.TryGetValue(username, out var state) && state.LockedUntil > DateTimeOffset.UtcNow)
            return (LoginResult.LockedOut, null);

        var user = Find(username);
        var hash = user is { Disabled: false } ? user.Value.Hash : DummyHash.Value;
        // Always run the hash verification so timing does not reveal whether the user exists.
        var verified = false;
        try
        {
            verified = Hasher.VerifyHashedPassword("", hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            _logger.LogError("Invalid password hash configured for user {User}", username);
        }

        if (user is not { Disabled: false } || !verified)
        {
            var failures = state.Failures + 1;
            DateTimeOffset? lockedUntil = failures >= auth.MaxFailedAttempts
                ? DateTimeOffset.UtcNow.AddMinutes(auth.LockoutMinutes)
                : null;
            _attempts[username] = (lockedUntil is null ? failures : 0, lockedUntil);
            _logger.LogWarning("Failed login for {User}{Locked}", username, lockedUntil is null ? "" : " (locked out)");
            return (lockedUntil is null ? LoginResult.InvalidCredentials : LoginResult.LockedOut, null);
        }

        _attempts.TryRemove(username, out _);
        _logger.LogInformation("User {User} signed in", user.Value.Username);
        return (LoginResult.Success, CreatePrincipal(user.Value.Username)!);
    }

    /// <summary>Builds the principal for an active user with its current roles, or null.</summary>
    public ClaimsPrincipal? CreatePrincipal(string username)
    {
        if (Find(username) is not { Disabled: false } u) return null;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, u.Username),
            new("display_name", u.DisplayName),
        };
        if (u.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>True when the user exists and is enabled.</summary>
    public bool IsActive(string? username) => username is not null && Find(username) is { Disabled: false };

    /// <summary>Current admin flag, read from the store (not from possibly stale cookie claims).</summary>
    public bool IsAdmin(string? username) => username is not null && Find(username) is { Disabled: false, IsAdmin: true };

    private (string Username, string DisplayName, string Hash, bool IsAdmin, bool Disabled, bool BuiltIn)? Find(string username)
    {
        var b = BuiltIn.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (b is not null)
            return (b.Username, b.DisplayName ?? b.Username, b.PasswordHash, b.Roles.Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase), false, true);
        lock (_lock)
        {
            var m = _managed.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            return m is null ? null : (m.Username, m.DisplayName ?? m.Username, m.PasswordHash, m.Role == Roles.Admin, m.Disabled, false);
        }
    }

    // ---------------- Management ----------------

    public IReadOnlyList<UserSummary> List()
    {
        var builtIn = BuiltIn.Select(b => new UserSummary(b.Username, b.DisplayName ?? b.Username,
            b.Roles.Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase), false, true, null, null));
        lock (_lock)
        {
            return builtIn.Concat(_managed.Select(m => new UserSummary(m.Username, m.DisplayName ?? m.Username,
                    m.Role == Roles.Admin, m.Disabled, false, m.CreatedAt, m.CreatedBy)))
                .OrderByDescending(u => u.IsBuiltIn).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]{1,63}$")]
    private static partial Regex UsernamePattern();

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"Password must be at least {MinPasswordLength} characters."
            : null;

    /// <summary>Creates a user. Returns an error message or null on success.</summary>
    public string? Create(string username, string? displayName, string password, bool isAdmin, string by)
    {
        username = username.Trim();
        if (!UsernamePattern().IsMatch(username))
            return "Username must be 2-64 characters: letters, digits, '.', '_', '-', '@'.";
        if (ValidatePassword(password) is { } pwdError) return pwdError;

        lock (_lock)
        {
            if (Find(username) is not null) return $"User '{username}' already exists.";
            var list = _managed.Append(new ManagedUser
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                PasswordHash = HashPassword(password),
                Role = isAdmin ? Roles.Admin : Roles.Viewer,
                CreatedAt = DateTimeOffset.Now,
                CreatedBy = by,
            }).ToList();
            Persist(list);
        }
        _logger.LogInformation("User {User} created by {By} ({Role})", username, by, isAdmin ? Roles.Admin : Roles.Viewer);
        Changed?.Invoke();
        return null;
    }

    public string? SetRole(string username, bool isAdmin, string by) =>
        Update(username, by, u =>
        {
            if (!isAdmin && u.Role == Roles.Admin && !OtherActiveAdminExists(u.Username)) return "At least one active administrator is required.";
            u.Role = isAdmin ? Roles.Admin : Roles.Viewer;
            return null;
        }, $"role set to {(isAdmin ? Roles.Admin : Roles.Viewer)}");

    public string? SetDisabled(string username, bool disabled, string by) =>
        Update(username, by, u =>
        {
            if (disabled && u.Username.Equals(by, StringComparison.OrdinalIgnoreCase)) return "You cannot disable your own account.";
            if (disabled && u.Role == Roles.Admin && !OtherActiveAdminExists(u.Username)) return "At least one active administrator is required.";
            u.Disabled = disabled;
            return null;
        }, disabled ? "disabled" : "enabled");

    public string? SetDisplayName(string username, string? displayName, string by) =>
        Update(username, by, u =>
        {
            u.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            return null;
        }, "display name changed");

    public string? ResetPassword(string username, string password, string by)
    {
        if (ValidatePassword(password) is { } error) return error;
        return Update(username, by, u =>
        {
            u.PasswordHash = HashPassword(password);
            return null;
        }, "password reset");
    }

    public string? Delete(string username, string by)
    {
        if (username.Equals(by, StringComparison.OrdinalIgnoreCase)) return "You cannot delete your own account.";
        lock (_lock)
        {
            var u = _managed.FirstOrDefault(m => m.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return BuiltIn.Any(b => b.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                ? "Built-in users are defined in the configuration and cannot be deleted here."
                : "User not found.";
            if (u.Role == Roles.Admin && !u.Disabled && !OtherActiveAdminExists(u.Username)) return "At least one active administrator is required.";
            Persist(_managed.Where(m => m != u).ToList());
        }
        _logger.LogInformation("User {User} deleted by {By}", username, by);
        Changed?.Invoke();
        return null;
    }

    private string? Update(string username, string by, Func<ManagedUser, string?> change, string what)
    {
        lock (_lock)
        {
            var current = _managed.FirstOrDefault(m => m.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (current is null) return BuiltIn.Any(b => b.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                ? "Built-in users are defined in the configuration and cannot be changed here."
                : "User not found.";

            // Work on a copy so a failed validation or write leaves the store untouched.
            var copy = new ManagedUser
            {
                Username = current.Username, DisplayName = current.DisplayName, PasswordHash = current.PasswordHash,
                Role = current.Role, Disabled = current.Disabled, CreatedAt = current.CreatedAt, CreatedBy = current.CreatedBy,
            };
            if (change(copy) is { } error) return error;
            Persist(_managed.Select(m => m == current ? copy : m).ToList());
        }
        _logger.LogInformation("User {User}: {What} by {By}", username, what, by);
        Changed?.Invoke();
        return null;
    }

    private bool OtherActiveAdminExists(string except) =>
        BuiltIn.Any(b => b.Roles.Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase) && !b.Username.Equals(except, StringComparison.OrdinalIgnoreCase))
        || _managed.Any(m => m.Role == Roles.Admin && !m.Disabled && !m.Username.Equals(except, StringComparison.OrdinalIgnoreCase));

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private void Persist(List<ManagedUser> list)
    {
        _store.Save(new Document { Users = list });
        _managed = list;
    }
}
