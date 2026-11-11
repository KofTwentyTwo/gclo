/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// The Credential Manager vault's error arms, driven through internal seams:
/// platform rejection, oversized blobs, and invalid target names. The happy
/// paths live in <see cref="VaultTests"/>. The real-store tests guard on
/// <see cref="OperatingSystem.IsWindows"/> (recognized by the platform analyzer);
/// CI runs windows-latest, where they execute.
/// </summary>
[SuppressMessage(
    "Interoperability", "CA1416:Validate platform compatibility",
    Justification = "Real-store tests guard on OperatingSystem.IsWindows at runtime (CI is windows-latest); "
        + "the not-supported test deliberately drives the non-Windows seam. The platform analyzer cannot see "
        + "the guard through the Assert.Throws lambdas.")]
public sealed class VaultEdgeTests
{
   [Fact]
   public void Constructor_NotWindows_ThrowsPlatformNotSupported()
       => Assert.Throws<PlatformNotSupportedException>(() => new CredentialManagerVault(isWindows: false));



   // ---------------------------------------------------------------- data-root scoping (#60)

   [Fact]
   public void ScopeFor_DefaultDataRoot_IsNull_SoExistingEntriesKeepTheirNames()
   {
      string root = Path.Combine(Path.GetTempPath(), "gclo-default");

      Assert.Null(CredentialManagerVault.ScopeFor(root, root));
      Assert.Null(CredentialManagerVault.ScopeFor(root + Path.DirectorySeparatorChar, root));
      Assert.Null(CredentialManagerVault.ScopeFor(root.ToUpperInvariant(), root.ToLowerInvariant()));
      Assert.Equal($"gclo:account:{Guid.Empty:N}", CredentialManagerVault.TargetName(Guid.Empty, null));
   }



   [Fact]
   public void ScopeFor_OtherDataRoot_IsAStableHash_ThatIgnoresCaseAndTrailingSeparators()
   {
      string def = Path.Combine(Path.GetTempPath(), "gclo-default");
      string other = Path.Combine(Path.GetTempPath(), "gclo-uitests", "abc");

      string? scope = CredentialManagerVault.ScopeFor(other, def);

      Assert.NotNull(scope);
      Assert.Matches("^[0-9a-f]{16}$", scope);
      Assert.Equal(scope, CredentialManagerVault.ScopeFor(other + "\\", def));
      Assert.Equal(scope, CredentialManagerVault.ScopeFor(other.ToUpperInvariant(), def));
      Assert.Equal(scope, CredentialManagerVault.ScopeFor(other.Replace('\\', '/'), def));
      Assert.NotEqual(scope, CredentialManagerVault.ScopeFor(other + "2", def), StringComparer.Ordinal);

      var id = Guid.NewGuid();
      Assert.Equal($"gclo:{scope}:account:{id:N}", CredentialManagerVault.TargetName(id, scope));
   }



   [Fact]
   public void AScopedVault_DoesNotSeeAnotherScopesEntry_AndCleansUpItsOwn()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      var id = Guid.NewGuid();
      // Two data roots, neither the one the installed app uses: a test stays out of
      // that namespace (#161). That the default data root has no scope in its entry
      // names is stated above, without storing anything.
      CredentialManagerVault one = NewTestVault();
      CredentialManagerVault other = NewTestVault();
      try
      {
         one.Store(id, "ghp_one_profile");
         Assert.Null(other.TryRetrieve(id)); // another data root never sees this one's token

         other.Store(id, "ghp_isolated");
         Assert.Equal("ghp_isolated", other.TryRetrieve(id));
         Assert.Equal("ghp_one_profile", one.TryRetrieve(id)); // and vice versa
      }
      finally
      {
         one.Delete(id);
         other.Delete(id);
      }
      Assert.Null(one.TryRetrieve(id));
      Assert.Null(other.TryRetrieve(id));
   }



   /// <summary>A vault whose entries are named for this test run alone, never in the namespace the app uses.</summary>
   private static CredentialManagerVault NewTestVault()
       => new(isWindows: true, scope: "tests-" + Guid.NewGuid().ToString("N")[..12]);



   [Fact]
   public void DefaultConstructor_NamesItsEntriesForTheDataFolder_AndFindsNothingForAnAccountNobodySaved()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      // The one test that uses the namespace of the real data folder, and it only reads.
      var vault = new CredentialManagerVault();

      Assert.Null(vault.TryRetrieve(Guid.NewGuid()));
   }



   [Fact]
   public void Store_TokenLargerThanCredManBlobLimit_SurfacesWin32Error()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      // CRED_MAX_CREDENTIAL_BLOB_SIZE is 2560 bytes; UTF-16 doubles the length.
      CredentialManagerVault vault = NewTestVault();
      string oversized = new('x', 4000);

      Win32Exception ex = Assert.Throws<Win32Exception>(() => vault.Store(Guid.NewGuid(), oversized));

      Assert.Contains("CredWriteW failed", ex.Message);
   }



   [Fact]
   public void TryRetrieve_InvalidTargetName_SurfacesWin32Error()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      CredentialManagerVault vault = NewTestVault();
      string invalidTarget = new('x', 40_000); // far past CRED_MAX_GENERIC_TARGET_NAME_LENGTH

      Win32Exception ex = Assert.Throws<Win32Exception>(() => vault.TryRetrieve(invalidTarget, Guid.NewGuid()));

      Assert.Contains("CredReadW failed", ex.Message);
   }



   [Fact]
   public void Delete_InvalidTargetName_SurfacesWin32Error()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      CredentialManagerVault vault = NewTestVault();
      string invalidTarget = new('x', 40_000);

      Win32Exception ex = Assert.Throws<Win32Exception>(() => vault.Delete(invalidTarget, Guid.NewGuid()));

      Assert.Contains("CredDeleteW failed", ex.Message);
   }
}
