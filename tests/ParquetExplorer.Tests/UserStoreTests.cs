using Microsoft.Extensions.Logging.Abstractions;
using ParquetExplorer.Options;
using ParquetExplorer.Services;

namespace ParquetExplorer.Tests;

public sealed class UserStoreTests : IDisposable
{
    private readonly string _state = TempDir.Create();

    private UserStore CreateStore(int maxAttempts = 3) => new(new StaticMonitor(new ExplorerOptions
    {
        StateDirectory = _state,
        Auth = new AuthOptions
        {
            MaxFailedAttempts = maxAttempts,
            LockoutMinutes = 5,
            Users = [new UserOptions { Username = "alice", PasswordHash = UserStore.HashPassword("s3cret"), Roles = [Roles.Admin] }],
        },
    }), NullLogger<UserStore>.Instance);

    public void Dispose() => TempDir.Delete(_state);

    [Fact]
    public void BuiltInAdmin_CanSignIn()
    {
        var (result, principal) = CreateStore().ValidateCredentials("ALICE", "s3cret");
        Assert.Equal(LoginResult.Success, result);
        Assert.Equal("alice", principal!.Identity!.Name);
        Assert.True(principal.IsInRole(Roles.Admin));
    }

    [Fact]
    public void WrongPassword_IsRejected_AndLocksOutAfterMaxAttempts()
    {
        var store = CreateStore(maxAttempts: 3);
        Assert.Equal(LoginResult.InvalidCredentials, store.ValidateCredentials("alice", "x").Result);
        Assert.Equal(LoginResult.InvalidCredentials, store.ValidateCredentials("alice", "y").Result);
        Assert.Equal(LoginResult.LockedOut, store.ValidateCredentials("alice", "z").Result);
        // Even the right password is refused while locked out.
        Assert.Equal(LoginResult.LockedOut, store.ValidateCredentials("alice", "s3cret").Result);
    }

    [Fact]
    public void UnknownUser_IsRejected() =>
        Assert.Equal(LoginResult.InvalidCredentials, CreateStore().ValidateCredentials("bob", "s3cret").Result);

    [Fact]
    public void ManagedUsers_ArePersisted_AndCanSignIn()
    {
        var store = CreateStore();
        Assert.Null(store.Create("bob", "Bob", "password1", isAdmin: false, by: "alice"));

        // A new instance reads users.json from disk.
        var reloaded = CreateStore();
        var (result, principal) = reloaded.ValidateCredentials("bob", "password1");
        Assert.Equal(LoginResult.Success, result);
        Assert.False(principal!.IsInRole(Roles.Admin));
        Assert.DoesNotContain("password1", File.ReadAllText(Path.Combine(_state, "users.json")));
    }

    [Fact]
    public void Create_ValidatesInput()
    {
        var store = CreateStore();
        Assert.NotNull(store.Create("alice", null, "password1", false, "alice"));   // clashes with built-in
        Assert.NotNull(store.Create("x", null, "password1", false, "alice"));       // too short
        Assert.NotNull(store.Create("bad name", null, "password1", false, "alice"));
        Assert.NotNull(store.Create("carol", null, "short", false, "alice"));       // weak password
    }

    [Fact]
    public void RoleChanges_AndDisable_ApplyImmediately()
    {
        var store = CreateStore();
        store.Create("bob", null, "password1", false, "alice");
        Assert.False(store.IsAdmin("bob"));

        Assert.Null(store.SetRole("bob", true, "alice"));
        Assert.True(store.IsAdmin("bob"));
        Assert.True(store.CreatePrincipal("bob")!.IsInRole(Roles.Admin));

        Assert.Null(store.SetDisabled("bob", true, "alice"));
        Assert.False(store.IsActive("bob"));
        Assert.Null(store.CreatePrincipal("bob"));
        Assert.Equal(LoginResult.InvalidCredentials, store.ValidateCredentials("bob", "password1").Result);
    }

    [Fact]
    public void BuiltInUsers_CannotBeChangedOrDeleted()
    {
        var store = CreateStore();
        Assert.NotNull(store.SetRole("alice", false, "someone"));
        Assert.NotNull(store.Delete("alice", "someone"));
    }

    [Fact]
    public void Users_CannotDeleteOrDisableThemselves()
    {
        var store = CreateStore();
        store.Create("bob", null, "password1", true, "alice");
        Assert.NotNull(store.Delete("bob", "bob"));
        Assert.NotNull(store.SetDisabled("bob", true, "bob"));
    }

    [Fact]
    public void ResetPassword_ReplacesTheOldOne()
    {
        var store = CreateStore();
        store.Create("bob", null, "password1", false, "alice");
        Assert.Null(store.ResetPassword("bob", "password2", "alice"));
        Assert.Equal(LoginResult.InvalidCredentials, store.ValidateCredentials("bob", "password1").Result);
        Assert.Equal(LoginResult.Success, store.ValidateCredentials("bob", "password2").Result);
    }
}
