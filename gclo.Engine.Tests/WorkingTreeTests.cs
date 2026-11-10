/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="WorkingTree"/> keeps path recovery's writes and deletes inside the
/// working directory, out of <c>.git</c>, and off links (#141).
/// </summary>
public sealed class WorkingTreeTests : IDisposable
{
   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly string _workdir;

   private readonly string _outside;

   private readonly List<string> _links = [];



   public WorkingTreeTests()
   {
      _workdir = Path.Combine(_root, "work");
      _outside = Path.Combine(_root, "outside");
      Directory.CreateDirectory(_workdir);
      Directory.CreateDirectory(_outside);
   }



   public void Dispose()
   {
      // Links first, as links: a recursive delete that meets a junction stops there
      // and leaves the folders above it behind.
      foreach(string link in _links)
      {
         RemoveLink(link);
      }
      TryDeleteDirectory(_root);
   }



   // ---------------------------------------------------------------- resolve

   [Theory]
   [InlineData("readme.txt")]
   [InlineData("src/app/main.cs")]
   [InlineData(".github/workflows/ci.yml")]
   [InlineData(".gitignore")]
   [InlineData(".gitattributes")]
   [InlineData("docs/git~notes.md")]
   [InlineData("git~2/file.txt")]
   [InlineData("GIT~10/file.txt")]
   [InlineData("git~x/file.txt")]
   [InlineData("name with spaces/and.dots.txt")]
   public void Resolve_PathInsideTheWorkingTree_IsItsFullPath(string repoPath)
   {
      string expected = Path.Combine(_workdir, repoPath.Replace('/', Path.DirectorySeparatorChar));

      Assert.Equal(expected, WorkingTree.Resolve(_workdir, repoPath));
      // A trailing separator on the working directory changes nothing.
      Assert.Equal(expected, WorkingTree.Resolve(_workdir + Path.DirectorySeparatorChar, repoPath));
   }



   [Theory]
   [InlineData("../outside/victim.txt")]
   [InlineData("src/../../outside/victim.txt")]
   [InlineData("..")]
   [InlineData(".")]
   [InlineData("src/..")]
   public void Resolve_PathThatLeavesTheWorkingTree_IsRefused(string repoPath)
   {
      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, repoPath));

      Assert.Contains("not a path inside the repository's working directory", ex.Message);
   }



   [Fact]
   public void Resolve_RootedPath_IsRefused_EvenInsideTheWorkingTreesParent()
   {
      // Path.Combine returns a rooted second argument unchanged: the classic way a
      // stored path escapes its base.
      string victim = Path.Combine(_outside, "victim.txt");

      Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, victim));
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, victim.Replace('\\', '/')));
      // A sibling whose name merely starts with the working directory's name.
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, "../work-other/file.txt"));
   }



   [Theory]
   [InlineData(".git/config")]
   [InlineData(".GIT/config")]
   [InlineData(".Git/hooks/pre-commit")]
   [InlineData("sub/module/.git/config")]
   [InlineData(".git")]
   [InlineData("git~1/config")]
   [InlineData("GIT~1/config")]
   [InlineData("sub/Git~1/config")]
   [InlineData("src/../.git/config")]
   [InlineData(".git./config")]
   [InlineData(".git /config")]
   [InlineData(".git.. ./config")]
   [InlineData("GIT~1./config")]
   [InlineData("sub/.GIT. /hooks/pre-commit")]
   public void Resolve_PathInsideAGitFolder_AtAnyDepthOrSpelling_IsRefused(string repoPath)
   {
      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, repoPath));

      Assert.Contains("'.git' folder", ex.Message);
   }



   [Theory]
   [InlineData("docs /victim.txt")]
   [InlineData("a/b. ./c.txt")]
   [InlineData("docs/")]
   [InlineData("notes.txt:stream")]
   [InlineData("folder:stream/file.txt")]
   public void Resolve_PartThatWindowsWouldOpenUnderAnotherName_IsRefused(string repoPath)
   {
      // "docs " and "docs" are two different folders to whoever checks the first and
      // one folder to Windows when it opens the second: such a name is never trusted.
      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorkingTree.Resolve(_workdir, repoPath));

      Assert.Contains("does not open under the name it is written with", ex.Message);
   }



   [Theory]
   [InlineData("", "file.txt")]
   [InlineData("  ", "file.txt")]
   public void Resolve_BlankWorkingDirectory_IsACallerError(string workdir, string repoPath)
   {
      Assert.Throws<ArgumentException>(() => WorkingTree.Resolve(workdir, repoPath));
   }



   [Theory]
   [InlineData("")]
   [InlineData("  ")]
   public void Resolve_BlankPath_IsACallerError(string repoPath)
   {
      Assert.Throws<ArgumentException>(() => WorkingTree.Resolve(_workdir, repoPath));
   }



   [Theory]
   [InlineData(".git", true)]
   [InlineData(".GIT", true)]
   [InlineData("git~1", true)]
   [InlineData("GIT~1", true)]
   [InlineData(".git.", true)]
   [InlineData(".git ", true)]
   [InlineData(".GIT. .", true)]
   [InlineData("git~1.", true)]
   [InlineData(".git::$INDEX_ALLOCATION", true)]
   [InlineData(".git:stream", true)]
   [InlineData("git~1::$INDEX_ALLOCATION", true)]
   [InlineData("git~2", false)] // a name git itself accepts, and GitHub can host
   [InlineData("GIT~27", false)]
   [InlineData("git~", false)]
   [InlineData("git~1a", false)]
   [InlineData(".gitx:stream", false)]
   [InlineData(".github", false)]
   [InlineData(".gitignore", false)]
   [InlineData("git", false)]
   [InlineData(".", false)]
   [InlineData(" ", false)]
   [InlineData("", false)]
   public void IsGitDirectoryName_TheNamesGitItselfRefuses_Only(string segment, bool expected)
   {
      Assert.Equal(expected, WorkingTree.IsGitDirectoryName(segment));
   }



   // ---------------------------------------------------------------- create

   [Fact]
   public void Create_WritesANewFile_AndItsFolders()
   {
      using(FileStream file = WorkingTree.Create(_workdir, "a/b/c.txt"))
      {
         file.Write("hello"u8);
      }

      Assert.Equal("hello", File.ReadAllText(Path.Combine(_workdir, "a", "b", "c.txt")));
   }



   [Fact]
   public void Create_ReplacesAnExistingFile_ReadOnlyOrNot()
   {
      string path = Path.Combine(_workdir, "locked.txt");
      File.WriteAllText(path, "a much longer previous content");
      File.SetAttributes(path, FileAttributes.ReadOnly);

      using(FileStream file = WorkingTree.Create(_workdir, "locked.txt"))
      {
         file.Write("new"u8);
      }

      Assert.Equal("new", File.ReadAllText(path));
   }



   [Fact]
   public void Create_ThroughAFolderThatIsALink_IsRefused_AndNothingIsWrittenOutside()
   {
      LinkDirectory(Path.Combine(_workdir, "vendor"), _outside);

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
          () => WorkingTree.Create(_workdir, "vendor/planted.txt"));

      Assert.Contains("is a link to another location", ex.Message);
      Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
   }



   [Fact]
   public void Create_ALinkDeeperOnTheWay_IsRefusedToo_EvenWithFoldersAlreadyVerified()
   {
      var verified = new HashSet<string>(StringComparer.Ordinal);
      using(WorkingTree.Create(_workdir, "src/app/main.cs", verified))
      {
      }
      Assert.Contains(Path.Combine(_workdir, "src"), verified);
      Assert.Contains(Path.Combine(_workdir, "src", "app"), verified);
      LinkDirectory(Path.Combine(_workdir, "src", "app", "vendor"), _outside);

      // The shared parents are remembered; the new folder is still checked.
      using(WorkingTree.Create(_workdir, "src/app/util.cs", verified))
      {
      }
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Create(_workdir, "src/app/vendor/planted.txt", verified));

      Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
   }



   [Fact]
   public void Create_WhereAHardLinkSits_ReplacesIt_AndLeavesTheOtherNameOfThatFileAlone()
   {
      // A hard link needs no privilege, and writing "in place" would change the file
      // under its other name too.
      string victim = Path.Combine(_outside, "victim.txt");
      File.WriteAllText(victim, "untouched");
      string link = Path.Combine(_workdir, "report.txt");
      HardLink(link, victim);

      using(FileStream file = WorkingTree.Create(_workdir, "report.txt"))
      {
         file.Write("payload"u8);
      }

      Assert.Equal("untouched", File.ReadAllText(victim));
      Assert.Equal("payload", File.ReadAllText(link));
   }



   [Fact]
   public void Create_WhereAFileLinkSits_ReplacesTheLink_AndLeavesItsTargetAlone()
   {
      string victim = Path.Combine(_outside, "victim.txt");
      File.WriteAllText(victim, "untouched");
      string link = Path.Combine(_workdir, "report.txt");
      try
      {
         File.CreateSymbolicLink(link, victim);
      }
      catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
      {
         return; // a file symlink needs a privilege (or Developer Mode) on Windows; the hard-link test above always runs
      }

      using(FileStream file = WorkingTree.Create(_workdir, "report.txt"))
      {
         file.Write("payload"u8);
      }

      Assert.Equal("untouched", File.ReadAllText(victim));
      Assert.Equal("payload", File.ReadAllText(link));
      Assert.Null(new FileInfo(link).LinkTarget);
   }



   [Fact]
   public void Create_WhereAFolderLinkSits_ReplacesTheLinkWithTheFile_AndLeavesItsTargetAlone()
   {
      File.WriteAllText(Path.Combine(_outside, "kept.txt"), "kept");
      string link = Path.Combine(_workdir, "report.txt");
      LinkDirectory(link, _outside);

      using(FileStream file = WorkingTree.Create(_workdir, "report.txt"))
      {
         file.Write("payload"u8);
      }

      Assert.Equal("payload", File.ReadAllText(link));
      Assert.Equal("kept", File.ReadAllText(Path.Combine(_outside, "kept.txt")));
   }



   [Fact]
   public void Create_OutsideOrInGit_IsRefused_BeforeAnythingIsTouched()
   {
      Directory.CreateDirectory(Path.Combine(_workdir, ".git"));

      Assert.Throws<InvalidOperationException>(() => WorkingTree.Create(_workdir, "../outside/planted.txt"));
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Create(_workdir, ".git/config"));
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Create(_workdir, "docs /planted.txt"));

      Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
      Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_workdir, ".git")));
      Assert.Single(Directory.EnumerateFileSystemEntries(_workdir)); // only .git
   }



   // ---------------------------------------------------------------- delete

   [Fact]
   public void Delete_RemovesTheFile_AndTheFoldersItLeavesEmpty_ButNotTheWorkingDirectory()
   {
      string nested = Path.Combine(_workdir, "a", "b", "c.txt");
      Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
      File.WriteAllText(nested, "x");
      File.SetAttributes(nested, FileAttributes.ReadOnly);

      WorkingTree.Delete(_workdir, "a/b/c.txt");

      Assert.False(Directory.Exists(Path.Combine(_workdir, "a")));
      Assert.True(Directory.Exists(_workdir));
   }



   [Fact]
   public void Delete_StopsPruningAtAFolderThatStillHoldsSomething()
   {
      Directory.CreateDirectory(Path.Combine(_workdir, "a", "b"));
      File.WriteAllText(Path.Combine(_workdir, "a", "b", "c.txt"), "x");
      File.WriteAllText(Path.Combine(_workdir, "a", "keep.txt"), "keep");

      WorkingTree.Delete(_workdir, "a/b/c.txt");

      Assert.False(Directory.Exists(Path.Combine(_workdir, "a", "b")));
      Assert.True(File.Exists(Path.Combine(_workdir, "a", "keep.txt")));
   }



   [Fact]
   public void Delete_MissingFile_IsFine()
   {
      WorkingTree.Delete(_workdir, "never/was/here.txt");
      WorkingTree.Delete(_workdir, "top-level.txt");

      Assert.Empty(Directory.EnumerateFileSystemEntries(_workdir));
   }



   [Fact]
   public void Delete_OutsideTheWorkingTree_IsRefused_AndTheFileSurvives()
   {
      string victim = Path.Combine(_outside, "victim.txt");
      File.WriteAllText(victim, "untouched");

      Assert.Throws<InvalidOperationException>(() => WorkingTree.Delete(_workdir, victim));
      Assert.Throws<InvalidOperationException>(() => WorkingTree.Delete(_workdir, "../outside/victim.txt"));

      Assert.Equal("untouched", File.ReadAllText(victim));
   }



   [Fact]
   public void Delete_ThroughAFolderThatIsALink_IsRefused_AndTheFileSurvives()
   {
      string victim = Path.Combine(_outside, "victim.txt");
      File.WriteAllText(victim, "untouched");
      LinkDirectory(Path.Combine(_workdir, "vendor"), _outside);

      Assert.Throws<InvalidOperationException>(() => WorkingTree.Delete(_workdir, "vendor/victim.txt"));

      Assert.Equal("untouched", File.ReadAllText(victim));
   }



   // ---------------------------------------------------------------- delete a directory

   [Fact]
   public void DeleteDirectory_RemovesEverything_ReadOnlyFilesAndFoldersIncluded()
   {
      string folder = Path.Combine(_workdir, "half-clone");
      string pack = Path.Combine(folder, ".git", "objects", "pack", "pack-1.idx");
      Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
      File.WriteAllText(pack, "x");
      File.SetAttributes(pack, FileAttributes.ReadOnly | FileAttributes.Hidden);
      File.WriteAllText(Path.Combine(folder, "readme.txt"), "x");
      new DirectoryInfo(Path.Combine(folder, ".git")).Attributes |= FileAttributes.Hidden | FileAttributes.ReadOnly;

      WorkingTree.DeleteDirectory(folder);

      Assert.False(Directory.Exists(folder));
   }



   [Fact]
   public void DeleteDirectory_ALinkInside_IsRemovedAsALink_AndWhatItPointsAtIsNeitherEnteredNorChanged()
   {
      string victim = Path.Combine(_outside, "victim.txt");
      File.WriteAllText(victim, "untouched");
      File.SetAttributes(victim, FileAttributes.ReadOnly);
      string folder = Path.Combine(_workdir, "half-clone");
      Directory.CreateDirectory(Path.Combine(folder, "src"));
      LinkDirectory(Path.Combine(folder, "src", "vendor"), _outside);

      WorkingTree.DeleteDirectory(folder);

      Assert.False(Directory.Exists(folder));
      Assert.Equal("untouched", File.ReadAllText(victim));
      Assert.True(File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly), "the linked folder's files keep their attributes");
      File.SetAttributes(victim, FileAttributes.Normal);
   }



   [Fact]
   public void DeleteDirectory_OfALink_RemovesOnlyTheLink()
   {
      File.WriteAllText(Path.Combine(_outside, "kept.txt"), "kept");
      string link = Path.Combine(_workdir, "linked");
      LinkDirectory(link, _outside);

      WorkingTree.DeleteDirectory(link);

      Assert.False(Directory.Exists(link));
      Assert.Equal("kept", File.ReadAllText(Path.Combine(_outside, "kept.txt")));
   }



   [Fact]
   public void DeleteDirectory_MissingFolder_IsFine_AndABlankPathIsACallerError()
   {
      WorkingTree.DeleteDirectory(Path.Combine(_workdir, "never-existed"));

      Assert.Throws<ArgumentException>(() => WorkingTree.DeleteDirectory(" "));
   }



   // ---------------------------------------------------------------- helpers

   /// <summary>
   /// Makes <paramref name="link"/> a directory link to <paramref name="target"/>: a
   /// junction on Windows (which, unlike a symbolic link, needs no privilege), a
   /// symbolic link elsewhere. The link is removed again when the test ends.
   /// </summary>
   private void LinkDirectory(string link, string target)
   {
      CreateDirectoryLink(link, target);
      _links.Add(link);
   }



   /// <summary>Creates a directory link without tracking it; the caller removes it with <see cref="RemoveLink"/>.</summary>
   internal static void CreateDirectoryLink(string link, string target)
   {
      if(OperatingSystem.IsWindows())
      {
         Run("cmd.exe", "/c", "mklink", "/J", link, target);
      }
      else
      {
         Directory.CreateSymbolicLink(link, target);
      }
      Assert.NotNull(new DirectoryInfo(link).LinkTarget);
   }



   /// <summary>Removes a directory link itself (never its target); fine when it is already gone.</summary>
   internal static void RemoveLink(string link)
   {
      var info = new DirectoryInfo(link);
      if(info.LinkTarget is not null)
      {
         info.Delete();
      }
   }



   private static void HardLink(string link, string target)
   {
      if(OperatingSystem.IsWindows())
      {
         Run("cmd.exe", "/c", "mklink", "/H", link, target);
      }
      else
      {
         Run("ln", target, link);
      }
   }



   private static void Run(string fileName, params string[] arguments)
   {
      var startInfo = new ProcessStartInfo(fileName)
      {
         UseShellExecute = false,
         CreateNoWindow = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
      };
      foreach(string argument in arguments)
      {
         startInfo.ArgumentList.Add(argument);
      }
      using Process process = Process.Start(startInfo)!;
      string error = process.StandardError.ReadToEnd();
      process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      Assert.True(process.ExitCode == 0, $"{fileName} {string.Join(' ', arguments)} failed: {error}");
   }
}
