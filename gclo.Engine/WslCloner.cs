/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// Drives <c>wsl.exe</c> for <see cref="IWslCloner"/>. Everything that runs inside the
/// distribution is a POSIX <c>sh</c> script fed through stdin (<c>sh -s</c>) with the
/// organization, repository, and URL as positional parameters, so no value is ever
/// re-parsed by wsl.exe's own command-line splitting and nothing needs quoting. The token
/// travels as <c>GCLO_GIT_TOKEN</c>, forwarded by <c>WSLENV</c>, and is read by an inline
/// credential helper: it appears in no argument vector and no file.
/// </summary>
public sealed class WslCloner : IWslCloner
{
   /// <summary>Environment variable the inline credential helper reads the token from.</summary>
   public const string TokenVariable = "GCLO_GIT_TOKEN";

   /// <summary>The folder under <c>~/gclo</c> in which clones are made before they are moved into place.</summary>
   internal const string TemporaryFolderName = ".gclo-tmp";

   /// <summary>
   /// The clone script. $1 organization, $2 repository, $3 clone URL, $4 the URL's
   /// host (with a non-default port). Prints the distribution name and the destination
   /// as its last two lines on success. A folder that already is a clone of the same
   /// URL is fast-forwarded instead of failing, so the verb is safe to use twice; a
   /// folder that is a clone of anything else is refused, and so is anything else at
   /// the destination but an empty folder. A new clone is made in a folder of gclo's
   /// own (<c>~/gclo/.gclo-tmp</c>) and moved into place when it is complete, so one
   /// that is cut off never leaves a folder that the next attempt would take for a
   /// clone and try to update. The temporary folder is removed when the script ends
   /// without having moved it, also when it is ended by a signal; what a killed
   /// script left there is removed by a later run once it is a day old (not sooner:
   /// another gclo may be cloning right now). A destination that appears while the
   /// clone runs is not moved into: the clone would end up inside it (checked before
   /// the move, and the move itself is <c>mv -T</c>, which renames onto the name and
   /// never into a folder of that name).
   /// The credential helper is
   /// scoped to $4, so git (and anything it starts, such as an LFS filter pointed at
   /// another server by the repository's own configuration) gets the token for that
   /// host only (#139).
   /// </summary>
   internal const string CloneScript =
       "set -e\n"
       + "org=\"$1\"; repo=\"$2\"; url=\"$3\"; host=\"$4\"\n"
       + "dest=\"$HOME/gclo/$org/$repo\"\n"
       + "helper='!f() { echo username=x-access-token; echo \"password=$" + TokenVariable + "\"; }; f'\n"
       + "if [ -d \"$dest/.git\" ]; then\n"
       + "  if [ \"$(git -C \"$dest\" config --get remote.origin.url)\" != \"$url\" ]; then\n"
       + "    echo \"$dest exists but is not a clone of this repository; move or delete it, then try again\" >&2\n"
       + "    exit 1\n"
       + "  fi\n"
       + "  git -C \"$dest\" -c credential.helper= -c \"credential.https://$host.helper=$helper\" pull --ff-only --quiet\n"
       + "else\n"
       + "  if [ -e \"$dest\" ] && ! rmdir \"$dest\" 2>/dev/null; then\n"
       + "    echo \"$dest exists but is not a repository; move or delete it, then try again\" >&2\n"
       + "    exit 1\n"
       + "  fi\n"
       + "  mkdir -p \"$HOME/gclo/$org\" \"$HOME/gclo/.gclo-tmp\"\n"
       + "  find \"$HOME/gclo/.gclo-tmp\" -mindepth 1 -maxdepth 1 -mmin +1440 -exec rm -rf {} + 2>/dev/null || true\n"
       + "  tmp=\"$HOME/gclo/.gclo-tmp/$org.$repo.$$\"\n"
       + "  rm -rf \"$tmp\"\n"
       + "  trap 'rm -rf \"$tmp\"' EXIT\n"
       + "  trap 'exit 129' HUP\n"
       + "  trap 'exit 130' INT\n"
       + "  trap 'exit 143' TERM\n"
       + "  git -c credential.helper= -c \"credential.https://$host.helper=$helper\" clone --quiet -- \"$url\" \"$tmp\"\n"
       + "  if [ -e \"$dest\" ]; then\n"
       + "    echo \"$dest appeared while the repository was being cloned (another gclo?); nothing was put there\" >&2\n"
       + "    exit 1\n"
       + "  fi\n"
       + "  mv -T \"$tmp\" \"$dest\"\n"
       + "  trap - EXIT HUP INT TERM\n"
       + "fi\n"
       + "printf '%s\\n%s\\n' \"$WSL_DISTRO_NAME\" \"$dest\"\n";

   private readonly IProcessRunner _runner;

   private readonly Func<string, bool> _fileExists;

   private readonly string _wslExe;



   /// <param name="runner">Process seam; the real <see cref="ProcessRunner"/> by default.</param>
   /// <param name="fileExists">File-existence probe for wsl.exe; <see cref="File.Exists"/> by default.</param>
   /// <param name="wslExePath">Path of wsl.exe; <c>%SystemRoot%\System32\wsl.exe</c> by default.</param>
   public WslCloner(IProcessRunner? runner = null, Func<string, bool>? fileExists = null, string? wslExePath = null)
   {
      _runner = runner ?? new ProcessRunner();
      _fileExists = fileExists ?? File.Exists;
      _wslExe = wslExePath ?? Path.Combine(Environment.SystemDirectory, "wsl.exe");
   }



   /// <inheritdoc />
   public async Task<WslAvailability> ProbeAsync(CancellationToken cancellationToken)
   {
      if(!_fileExists(_wslExe))
      {
         return new WslAvailability(false, "wsl.exe is not installed (Windows Subsystem for Linux is not enabled).");
      }

      ProcessResult result;
      try
      {
         result = await _runner.RunAsync(_wslExe, ["-e", "git", "--version"], null, null, cancellationToken).ConfigureAwait(false);
      }
      catch(OperationCanceledException)
      {
         throw;
      }
      catch(Exception ex)
      {
         return new WslAvailability(false, $"wsl.exe could not be started: {ex.Message}");
      }

      string stdout = result.StandardOutput.Trim();
      if(result.ExitCode == 0 && stdout.StartsWith("git version", StringComparison.Ordinal))
      {
         return new WslAvailability(true, stdout);
      }

      string reason = FirstLine(result.StandardError) ?? FirstLine(stdout) ?? $"exit code {result.ExitCode}";
      return new WslAvailability(false, result.ExitCode == 0
          ? $"git is not installed in the default WSL distribution ({reason})."
          : $"the default WSL distribution did not run git: {reason}");
   }



   /// <inheritdoc />
   public async Task<WslCloneResult> CloneAsync(string url, string organization, string repositoryName, string token, CancellationToken cancellationToken)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(url);
      ArgumentException.ThrowIfNullOrWhiteSpace(organization);
      ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
      ArgumentNullException.ThrowIfNull(token);

      /////////////////////////////////////////////////////////////////////////////
      // The organization and repository become path segments under ~/gclo, and    //
      // the URL's host is what the token is scoped to. None of the three is taken //
      // on trust: a name with a separator or ".." would leave ~/gclo, and a URL   //
      // that is not HTTPS has no host the token could safely be tied to (#139).   //
      /////////////////////////////////////////////////////////////////////////////
      foreach(string name in new[] { organization, repositoryName })
      {
         if(!RepositoryPathResolver.TryValidateName(name, out string? reason))
         {
            throw new WslCloneException($"Clone in WSL refused: '{name}' cannot be used as a folder name ({reason}).");
         }
      }
      if(string.Equals(organization, TemporaryFolderName, StringComparison.OrdinalIgnoreCase))
      {
         ////////////////////////////////////////////////////////////////////////
         // ~/gclo/.gclo-tmp is where unfinished clones are made, and old ones //
         // there are removed.                                                 //
         ////////////////////////////////////////////////////////////////////////
         throw new WslCloneException($"Clone in WSL refused: '{organization}' is the name of gclo's own temporary folder there.");
      }

      string host = RemoteUrl.HttpsAuthority(url)
          ?? throw new WslCloneException("Clone in WSL refused: the clone URL is not an HTTPS URL.");

      var environment = new Dictionary<string, string>(StringComparer.Ordinal)
      {
         [TokenVariable] = token,
         ["GIT_TERMINAL_PROMPT"] = "0",
         // Forward both into the distribution (WSLENV is a colon-separated list).
         ["WSLENV"] = TokenVariable + ":GIT_TERMINAL_PROMPT",
      };

      ProcessResult result;
      try
      {
         result = await _runner.RunAsync(
             _wslExe,
             ["-e", "sh", "-s", organization, repositoryName, url, host],
             environment,
             CloneScript,
             cancellationToken).ConfigureAwait(false);
      }
      catch(OperationCanceledException)
      {
         throw;
      }
      catch(Exception ex)
      {
         throw new WslCloneException($"wsl.exe could not be started: {ex.Message}");
      }

      if(result.ExitCode != 0)
      {
         string reason = LastLine(result.StandardError) ?? LastLine(result.StandardOutput) ?? $"exit code {result.ExitCode}";
         throw new WslCloneException($"Clone in WSL failed: {reason}");
      }

      string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if(lines.Length < 2 || lines[^1].Length == 0 || lines[^2].Length == 0)
      {
         throw new WslCloneException("Clone in WSL finished but did not report where the repository was placed.");
      }

      string distribution = lines[^2];
      string linuxPath = lines[^1];
      string windowsPath = @"\\wsl.localhost\" + distribution + linuxPath.Replace('/', '\\');
      return new WslCloneResult(distribution, linuxPath, windowsPath);
   }



   private static string? FirstLine(string text)
       => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();



   private static string? LastLine(string text)
       => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
