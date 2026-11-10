/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// The only way path recovery writes or deletes a file. A git checkout refuses to
/// leave the working directory, to write into <c>.git</c>, or to follow a link; the
/// recovery materializes the tree itself, so it has to keep the same three promises.
/// Every path it handles comes from somewhere that is not trusted: the repository's
/// tree, a rename the user typed, or a file on disk that anything could have edited
/// (#141).
/// </summary>
/// <remarks>
/// The link check and the write that follows it are two steps. Something that swaps
/// a folder for a link between them, on the user's own machine and with the user's
/// own rights, is outside what this guards against.
/// </remarks>
internal static class WorkingTree
{
   /// <summary>
   /// The full path of <paramref name="repoPath"/> (forward-slash separated, relative)
   /// under <paramref name="workdir"/>. Throws unless the path, once resolved, is
   /// strictly inside the working directory and every segment of it is a name that
   /// Windows opens literally: not empty, no trailing dot or space (Windows drops
   /// those, so <c>docs </c> and <c>docs</c> would be two spellings of one check and
   /// two different folders on disk), no colon (a stream of another file), and not
   /// git's own folder (see <see cref="IsGitDirectoryName"/>).
   /// </summary>
   public static string Resolve(string workdir, string repoPath)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(workdir);
      ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

      string root = Root(workdir);
      string full = Path.GetFullPath(Path.Combine(root, repoPath.Replace('/', Path.DirectorySeparatorChar)));

      if(!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
      {
         throw new InvalidOperationException($"'{repoPath}' is not a path inside the repository's working directory.");
      }

      foreach(string segment in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
      {
         if(IsGitDirectoryName(segment))
         {
            throw new InvalidOperationException($"'{repoPath}' is inside a '.git' folder, which belongs to git, not to the working tree.");
         }

         if(segment.Length == 0 || segment[^1] is '.' or ' ' || segment.Contains(':', StringComparison.Ordinal))
         {
            throw new InvalidOperationException(
                $"'{repoPath}' has a part that Windows does not open under the name it is written with "
                + "(empty, ending in a dot or a space, or containing a colon).");
         }
      }

      return full;
   }



   /// <summary>
   /// True for the names git itself refuses in a tree because Windows opens them as
   /// the repository's own folder: <c>.git</c> in any letter case, and <c>GIT~1</c>,
   /// the short name NTFS gives it; either with trailing dots or spaces, which Windows
   /// drops; either followed by a stream name (<c>.git::$INDEX_ALLOCATION</c>), which
   /// names the same folder. The rule is the same on every platform, so a path's
   /// answer does not depend on where it is asked. (Should NTFS have given
   /// <c>.git</c> another short name, <see cref="Resolve"/> still refuses it:
   /// resolving a path expands short names.)
   /// </summary>
   public static bool IsGitDirectoryName(string segment)
   {
      ArgumentNullException.ThrowIfNull(segment);

      int stream = segment.IndexOf(':', StringComparison.Ordinal);
      ReadOnlySpan<char> name = (stream < 0 ? segment : segment[..stream]).AsSpan().TrimEnd(". ");
      return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
          || name.Equals("git~1", StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>
   /// Everything <see cref="Create"/> checks before it touches the disk, without
   /// touching it: the path resolves inside the working directory, and no folder on
   /// the way is a link. For finding out up front, before an operation that cannot be
   /// undone begins, whether its writes can succeed.
   /// </summary>
   public static void Verify(string workdir, string repoPath, ISet<string>? verifiedFolders = null)
       => RefuseLinkOnTheWay(workdir, Resolve(workdir, repoPath), verifiedFolders);



   /// <summary>
   /// Opens <paramref name="repoPath"/> for writing as a new, empty file, creating its
   /// folders. Whatever file is already there is removed first, read-only or not, so
   /// the content never goes through a link (symbolic or hard) left in its place.
   /// </summary>
   /// <param name="workdir">The repository's working directory.</param>
   /// <param name="repoPath">Forward-slash separated path relative to it.</param>
   /// <param name="verifiedFolders">
   /// Folders already found not to be links during the same operation; pass one set
   /// to every call of one materialization so shared parent folders are checked once.
   /// </param>
   public static FileStream Create(string workdir, string repoPath, ISet<string>? verifiedFolders = null)
   {
      string full = Resolve(workdir, repoPath);
      RefuseLinkOnTheWay(workdir, full, verifiedFolders);

      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      RemoveFile(full);
      return new FileStream(full, FileMode.CreateNew, FileAccess.Write);
   }



   /// <summary>
   /// Deletes the file <paramref name="repoPath"/> (a missing one is fine) and then
   /// the folders it leaves empty, stopping at the working directory itself. A folder
   /// that sits there now is left alone with everything in it: the file gclo wrote
   /// is gone already, and whoever put the folder there owns it.
   /// </summary>
   public static void Delete(string workdir, string repoPath)
   {
      string full = Resolve(workdir, repoPath);
      RefuseLinkOnTheWay(workdir, full, verifiedFolders: null);
      var folder = new DirectoryInfo(full);
      if(folder.Exists && folder.LinkTarget is null)
      {
         return;
      }
      RemoveFile(full);

      string root = Root(workdir);
      string? directory = Path.GetDirectoryName(full);
      while(directory is not null
          && !string.Equals(Path.TrimEndingDirectorySeparator(directory), root, PathComparison)
          && Directory.Exists(directory)
          && !Directory.EnumerateFileSystemEntries(directory).Any())
      {
         Directory.Delete(directory);
         directory = Path.GetDirectoryName(directory);
      }
   }



   /// <summary>
   /// Removes <paramref name="directory"/> and everything in it, read-only or not,
   /// without ever leaving it: a link inside (symbolic link or junction) is removed
   /// as a link, and what it points at is neither entered nor changed. A missing
   /// directory is fine.
   /// </summary>
   public static void DeleteDirectory(string directory)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(directory);

      var folder = new DirectoryInfo(directory);
      if(folder.LinkTarget is not null)
      {
         folder.Delete(); // the link itself
         return;
      }

      if(!folder.Exists)
      {
         return;
      }

      foreach(DirectoryInfo child in folder.EnumerateDirectories())
      {
         DeleteDirectory(child.FullName);
      }

      foreach(FileInfo file in folder.EnumerateFiles())
      {
         RemoveFile(file.FullName);
      }

      folder.Attributes = FileAttributes.Directory;
      folder.Delete();
   }



   private static StringComparison PathComparison
       => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;



   private static string Root(string workdir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workdir));



   /// <summary>
   /// A folder between the working directory and <paramref name="full"/> that is a
   /// link (a symbolic link or a junction) would carry the write or delete somewhere
   /// else. Nothing in a tree gclo materializes is ever a link, so one on the way is
   /// refused. A link is recognized by having a target, not by the reparse-point
   /// attribute, which cloud-synced folders carry without leading anywhere.
   /// </summary>
   private static void RefuseLinkOnTheWay(string workdir, string full, ISet<string>? verifiedFolders)
   {
      string root = Root(workdir);
      string current = root;
      string[] segments = full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
      for(int i = 0; i < segments.Length - 1; i++)
      {
         current = Path.Combine(current, segments[i]);
         if(verifiedFolders?.Contains(current) == true)
         {
            continue;
         }

         if(new DirectoryInfo(current).LinkTarget is not null)
         {
            throw new InvalidOperationException(
                $"'{current}' is a link to another location, so gclo will not write or delete through it. "
                + "Remove the link, then try again.");
         }

         verifiedFolders?.Add(current);
      }
   }



   /// <summary>
   /// The files inside the real folder at <paramref name="repoPath"/>, as paths
   /// relative to the working directory; nothing when no folder is there, or a link
   /// is. Links inside it are not entered and not listed.
   /// </summary>
   public static IEnumerable<string> FilesUnder(string workdir, string repoPath)
   {
      var folder = new DirectoryInfo(Resolve(workdir, repoPath));
      return folder.Exists && folder.LinkTarget is null ? FilesIn(folder, repoPath) : [];
   }



   private static IEnumerable<string> FilesIn(DirectoryInfo folder, string repoPath)
   {
      foreach(FileInfo file in folder.EnumerateFiles())
      {
         yield return repoPath + "/" + file.Name;
      }

      foreach(DirectoryInfo child in folder.EnumerateDirectories())
      {
         if(child.LinkTarget is not null)
         {
            continue;
         }
         foreach(string inner in FilesIn(child, repoPath + "/" + child.Name))
         {
            yield return inner;
         }
      }
   }



   /// <summary>
   /// Removes a folder that holds nothing but empty folders and links, bottom up.
   /// Called only for a folder already found to hold no file.
   /// </summary>
   private static void RemoveEmptyFolders(DirectoryInfo folder)
   {
      foreach(DirectoryInfo child in folder.EnumerateDirectories())
      {
         if(child.LinkTarget is not null)
         {
            child.Delete(); // a link is nobody's content; remove the link itself
            continue;
         }
         RemoveEmptyFolders(child);
      }

      folder.Attributes = FileAttributes.Directory;
      folder.Delete();
   }



   /// <summary>
   /// Removes what is at <paramref name="full"/> when it is a file, a link, or a folder
   /// that holds no files, read-only or not: a link is removed itself (even one whose
   /// target is gone, even one to a directory), never what it points at. A folder that
   /// still holds files is refused.
   /// </summary>
   private static void RemoveFile(string full)
   {
      var directory = new DirectoryInfo(full);
      if(directory.LinkTarget is not null && directory.Attributes.HasFlag(FileAttributes.Directory))
      {
         directory.Delete(); // a junction or a symbolic link to a directory
         return;
      }

      if(directory.Exists)
      {
         // A real folder where a file belongs (upstream turned a folder into a file).
         // Whatever the tree had in it is gone by now; if files remain they are not
         // gclo's to remove, and empty folders are nobody's. Looked at as a whole
         // first: a folder that is refused loses nothing, not even an empty subfolder.
         if(FilesIn(directory, directory.Name).Any())
         {
            throw new InvalidOperationException(
                $"'{directory.FullName}' is a folder with files in it, where the repository has a file. "
                + "Move or delete the folder, then try again.");
         }
         RemoveEmptyFolders(directory);
         return;
      }

      var file = new FileInfo(full);
      bool isLink = file.LinkTarget is not null;
      if(!file.Exists && !isLink)
      {
         return;
      }

      // Clearing read-only on a link acts on the link itself on Windows, but on what
      // it points at elsewhere; there a link is deleted whatever its mode says.
      if(!isLink || OperatingSystem.IsWindows())
      {
         file.Attributes = FileAttributes.Normal;
      }

      File.Delete(full);
   }
}
