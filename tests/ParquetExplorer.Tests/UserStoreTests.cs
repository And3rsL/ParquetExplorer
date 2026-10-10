using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;
using ParquetExplorer.Services;

namespace ParquetExplorer.Tests;

public class UserStoreTests
{
    private static UserStore CreateStore(int maxAttempts = 3)
    {
        var opts = new ExplorerOptions
        {
            Auth = new AuthOptions
            {
                MaxFailedAttempts = maxAttempts,
                LockoutMinutes = 5,
                Users = [new UserOptions { Username = "alice", PasswordHash = UserStore.HashPassword("s3cret"), Roles = ["Admin"] }],
            },
        };
        return new UserStore(new StaticMonitor(opts), NullLogger<UserStore>.Instance);
    }

    [Fact]
    public void ValidCredentials_ReturnPrincipalWithRoles()
    {
        var (result, principal) = CreateStore().ValidateCredentials("ALICE", "s3cret");
        Assert.Equal(LoginResult.Success, result);
        Assert.Equal("alice", principal!.Identity!.Name);
        Assert.True(principal.IsInRole("Admin"));
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

    private sealed class StaticMonitor(ExplorerOptions value) : IOptionsMonitor<ExplorerOptions>
    {
        public ExplorerOptions CurrentValue => value;
        public ExplorerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ExplorerOptions, string?> listener) => null;
    }
}
