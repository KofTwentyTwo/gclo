using gclo.ViewModels;

namespace gclo.Engine.Tests;

/// <summary>
/// Contract tests for <see cref="InMemoryVault"/> plus one real round-trip through
/// <see cref="CredentialManagerVault"/> against the Windows Credential Manager.
/// </summary>
public sealed class VaultTests
{
    // ---------------------------------------------------------------- InMemoryVault

    [Fact]
    public void InMemory_StoreThenRetrieve_ReturnsTheToken()
    {
        var vault = new InMemoryVault();
        var id = Guid.NewGuid();

        vault.Store(id, "ghp_secret");

        Assert.Equal("ghp_secret", vault.TryRetrieve(id));
    }

    [Fact]
    public void InMemory_Store_OverwritesTheExistingToken()
    {
        var vault = new InMemoryVault();
        var id = Guid.NewGuid();
        vault.Store(id, "ghp_old");

        vault.Store(id, "ghp_new");

        Assert.Equal("ghp_new", vault.TryRetrieve(id));
    }

    [Fact]
    public void InMemory_TryRetrieve_AbsentEntry_ReturnsNull()
    {
        Assert.Null(new InMemoryVault().TryRetrieve(Guid.NewGuid()));
    }

    [Fact]
    public void InMemory_Delete_RemovesTheToken_AndIsIdempotent()
    {
        var vault = new InMemoryVault();
        var id = Guid.NewGuid();
        vault.Store(id, "ghp_secret");

        vault.Delete(id);
        Assert.Null(vault.TryRetrieve(id));

        vault.Delete(id); // deleting an absent entry must not throw
    }

    // ---------------------------------------------------------------- CredentialManagerVault

    [Fact]
    public void CredentialManager_RoundTrips_AgainstTheRealStore()
    {
        // xunit 2.9 has no runtime skip, so on non-Windows this silently passes;
        // CI runs windows-latest, where the body always executes.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vault = new CredentialManagerVault();
        var id = Guid.NewGuid();
        try
        {
            vault.Store(id, "ghp_round-trip-secret");
            Assert.Equal("ghp_round-trip-secret", vault.TryRetrieve(id));

            vault.Store(id, "ghp_rotated");
            Assert.Equal("ghp_rotated", vault.TryRetrieve(id));

            vault.Delete(id);
            Assert.Null(vault.TryRetrieve(id));

            vault.Delete(id); // deleting an absent entry must not throw
        }
        finally
        {
            vault.Delete(id);
        }
    }
}
