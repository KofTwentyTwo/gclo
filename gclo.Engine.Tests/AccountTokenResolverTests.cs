/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="AccountTokenResolver"/>: an account's own entry for <see cref="TokenSource.Own"/>,
/// the default token for <see cref="TokenSource.Default"/>, null (with a source-specific
/// description) when the source holds nothing.
/// </summary>
public sealed class AccountTokenResolverTests
{
   private readonly InMemoryVault _vault = new();



   private static Account MakeAccount(TokenSource source) => new()
   {
      Id = Guid.NewGuid(),
      Name = "work",
      Organization = "acme",
      TargetRoot = @"C:\repos",
      TokenSource = source,
   };



   [Fact]
   public void NullVault_Throws()
       => Assert.Throws<ArgumentNullException>(() => new AccountTokenResolver(null!));



   [Fact]
   public void NullAccount_Throws()
   {
      var resolver = new AccountTokenResolver(_vault);

      Assert.Throws<ArgumentNullException>(() => resolver.TryResolve(null!));
      Assert.Throws<ArgumentNullException>(() => AccountTokenResolver.MissingDescription(null!));
   }



   [Fact]
   public void OwnAccount_ResolvesItsOwnEntry_NotTheDefault()
   {
      Account account = MakeAccount(TokenSource.Own);
      _vault.Store(account.Id, "ghp_own");
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");

      Assert.Equal("ghp_own", new AccountTokenResolver(_vault).TryResolve(account));
   }



   [Fact]
   public void DefaultAccount_ResolvesTheDefaultToken_EvenWhenAnOwnEntryLingers()
   {
      Account account = MakeAccount(TokenSource.Default);
      _vault.Store(account.Id, "ghp_stale");
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");

      Assert.Equal("ghp_default", new AccountTokenResolver(_vault).TryResolve(account));
   }



   [Fact]
   public void DefaultAccount_FollowsRotationOfTheDefaultToken()
   {
      Account account = MakeAccount(TokenSource.Default);
      var resolver = new AccountTokenResolver(_vault);
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_first");
      Assert.Equal("ghp_first", resolver.TryResolve(account));

      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_rotated");

      Assert.Equal("ghp_rotated", resolver.TryResolve(account));
   }



   [Theory]
   [InlineData(TokenSource.Own)]
   [InlineData(TokenSource.Default)]
   public void MissingSource_ResolvesNull(TokenSource source)
   {
      Account account = MakeAccount(source);

      Assert.Null(new AccountTokenResolver(_vault).TryResolve(account));
   }



   [Fact]
   public void EmptyStoredValue_CountsAsMissing()
   {
      Account account = MakeAccount(TokenSource.Own);
      _vault.Store(account.Id, "");

      Assert.Null(new AccountTokenResolver(_vault).TryResolve(account));
   }



   [Fact]
   public void HasDefaultToken_ReflectsTheVault()
   {
      var resolver = new AccountTokenResolver(_vault);
      Assert.False(resolver.HasDefaultToken);

      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");

      Assert.True(resolver.HasDefaultToken);
   }



   [Fact]
   public void MissingDescription_NamesTheSource_AndNeverATokenValue()
   {
      Account own = MakeAccount(TokenSource.Own);
      Account byDefault = MakeAccount(TokenSource.Default);

      string ownText = AccountTokenResolver.MissingDescription(own);
      string defaultText = AccountTokenResolver.MissingDescription(byDefault);

      Assert.Contains($"gclo:account:{own.Id:N}", ownText);
      Assert.Contains("Settings", defaultText);
      Assert.DoesNotContain("ghp", ownText + defaultText);
   }
}
