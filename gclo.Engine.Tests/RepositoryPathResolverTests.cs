/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="RepositoryPathResolver"/> is the trust boundary between caller-supplied
/// repository names and the file system: every name must be exactly one safe folder
/// segment, and the resolved folder must sit directly under the target root.
/// </summary>
public sealed class RepositoryPathResolverTests
{
   private static readonly string s_root = Path.Combine(Path.GetTempPath(), "gclo-tests", "resolver-root");



   // ---------------------------------------------------------------- valid names

   [Theory]
   [InlineData("gclo")]
   [InlineData("my-repo")]
   [InlineData("my_repo")]
   [InlineData("my.repo")]
   [InlineData("Repo.Name-With_Everything.v2")]
   [InlineData("123")]
   [InlineData(".github")]
   [InlineData("...dots")]
   [InlineData("name with spaces")]
   public void Resolve_ValidGitHubStyleName_ReturnsFolderDirectlyUnderRoot(string name)
   {
      string resolved = RepositoryPathResolver.Resolve(s_root, name);

      Assert.Equal(Path.Combine(Path.GetFullPath(s_root), name), resolved);
      Assert.Equal(Path.GetFullPath(s_root), Path.GetDirectoryName(resolved));
      Assert.True(Path.IsPathRooted(resolved));
   }



   [Fact]
   public void Resolve_RootWithTrailingSeparator_ResolvesIdentically()
   {
      string withSeparator = s_root + Path.DirectorySeparatorChar;

      Assert.Equal(
          RepositoryPathResolver.Resolve(s_root, "alpha"),
          RepositoryPathResolver.Resolve(withSeparator, "alpha"));
   }



   [Fact]
   public void Resolve_RelativeRoot_ResolvesAgainstCurrentDirectory()
   {
      string resolved = RepositoryPathResolver.Resolve("relative-root", "alpha");

      Assert.Equal(Path.Combine(Path.GetFullPath("relative-root"), "alpha"), resolved);
   }



   [Fact]
   public void Resolve_RootContainingDotSegments_IsNormalizedBeforeContainmentCheck()
   {
      string messyRoot = Path.Combine(s_root, "sub", "..");

      string resolved = RepositoryPathResolver.Resolve(messyRoot, "alpha");

      Assert.Equal(Path.Combine(Path.GetFullPath(s_root), "alpha"), resolved);
   }



   [Theory]
   [InlineData("gclo")]
   [InlineData("my-repo.v2")]
   public void TryValidateName_ValidName_ReturnsTrueWithNoReason(string name)
   {
      Assert.True(RepositoryPathResolver.TryValidateName(name, out string? reason));
      Assert.Null(reason);
   }



   // ---------------------------------------------------------------- rejected names

   [Theory]
   [InlineData(null, "empty")]
   [InlineData("", "empty")]
   [InlineData("   ", "empty")]
   [InlineData(".", "current or parent")]
   [InlineData("..", "current or parent")]
   [InlineData("../escape", "separator")]
   [InlineData("..\\escape", "separator")]
   [InlineData("nested/child", "separator")]
   [InlineData("nested\\child", "separator")]
   [InlineData("trailing/", "separator")]
   [InlineData("/etc", "separator")]
   [InlineData("\\\\server\\share", "separator")]
   [InlineData("C:\\Windows", "separator")]
   [InlineData("C:", ":")]
   [InlineData("C:repo", ":")]
   [InlineData("name:stream", ":")]
   [InlineData("tab\there", "not allowed")]
   [InlineData("nul\0byte", "not allowed")]
   public void TryValidateName_UnsafeName_ReturnsFalseWithReason(string? name, string expectedReasonFragment)
   {
      Assert.False(RepositoryPathResolver.TryValidateName(name, out string? reason));
      Assert.NotNull(reason);
      Assert.Contains(expectedReasonFragment, reason);
   }



   [Theory]
   [InlineData("../escape")]
   [InlineData("..\\escape")]
   [InlineData("nested/child")]
   [InlineData("C:\\Windows")]
   [InlineData("/etc")]
   [InlineData("\\\\server\\share")]
   [InlineData("..")]
   [InlineData("")]
   public void Resolve_UnsafeName_ThrowsArgumentExceptionNamingTheRepo(string name)
   {
      ArgumentException ex = Assert.Throws<ArgumentException>(() => RepositoryPathResolver.Resolve(s_root, name));

      Assert.Equal("repoName", ex.ParamName);
      Assert.Contains($"'{name}'", ex.Message);
   }



   [Theory]
   [InlineData("")]
   [InlineData("   ")]
   public void Resolve_BlankRoot_ThrowsArgumentException(string root)
   {
      Assert.ThrowsAny<ArgumentException>(() => RepositoryPathResolver.Resolve(root, "alpha"));
   }



   [Fact]
   public void Resolve_NullRoot_ThrowsArgumentNullException()
   {
      Assert.Throws<ArgumentNullException>(() => RepositoryPathResolver.Resolve(null!, "alpha"));
   }



   // ---------------------------------------------------------------- containment (the backstop)

   [Fact]
   public void Resolve_NameThatNormalizesOntoTheRootItself_IsRejected()
   {
      // "..." passes the textual checks (it is not "." or ".." and has no separator),
      // but Windows path normalization trims trailing dots so it would resolve to the
      // root itself. The containment check must catch it on Windows; elsewhere "..."
      // is a legitimate folder name.
      if(!OperatingSystem.IsWindows())
      {
         Assert.Equal(Path.Combine(Path.GetFullPath(s_root), "..."), RepositoryPathResolver.Resolve(s_root, "..."));
         return;
      }

      ArgumentException ex = Assert.Throws<ArgumentException>(() => RepositoryPathResolver.Resolve(s_root, "..."));

      Assert.Contains("not inside the target root", ex.Message);
   }



   [Fact]
   public void Resolve_IsCaseInsensitiveOnWindows_ForTheRootPrefix()
   {
      if(!OperatingSystem.IsWindows())
      {
         return;
      }

      // The root's own casing must not matter for containment on Windows.
      string resolved = RepositoryPathResolver.Resolve(s_root.ToUpperInvariant(), "alpha");

      Assert.EndsWith(Path.DirectorySeparatorChar + "alpha", resolved);
   }
}
