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

   /// <summary>
   /// The clone script. $1 organization, $2 repository, $3 clone URL. Prints the
   /// distribution name and the destination as its last two lines on success. A folder
   /// that already is a repository is fast-forwarded instead of failing, so the verb is
   /// safe to use twice.
   /// </summary>
   internal const string CloneScript =
       "set -e\n"
       + "org=\"$1\"; repo=\"$2\"; url=\"$3\"\n"
       + "dest=\"$HOME/gclo/$org/$repo\"\n"
       + "helper='!f() { echo username=x-access-token; echo \"password=$" + TokenVariable + "\"; }; f'\n"
       + "if [ -d \"$dest/.git\" ]; then\n"
       + "  git -C \"$dest\" -c credential.helper= -c \"credential.helper=$helper\" pull --ff-only --quiet\n"
       + "else\n"
       + "  mkdir -p \"$HOME/gclo/$org\"\n"
       + "  git -c credential.helper= -c \"credential.helper=$helper\" clone --quiet \"$url\" \"$dest\"\n"
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
             ["-e", "sh", "-s", organization, repositoryName, url],
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
