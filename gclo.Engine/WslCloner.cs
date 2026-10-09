using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace gclo.Engine;

/// <summary>Outcome of one external process: exit code plus captured output.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs an external process to completion. The seam that keeps <see cref="WslCloner"/>
/// testable without a WSL installation: the offline suite scripts results through a fake.
/// </summary>
public interface IProcessRunner
{
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Argument vector (quoted by the runner, never by the caller).</param>
    /// <param name="environment">Extra environment variables for the child; the parent's are inherited.</param>
    /// <param name="standardInput">Text written to the child's stdin, then closed; null leaves stdin closed.</param>
    /// <param name="cancellationToken">Kills the child and throws <see cref="OperationCanceledException"/>.</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken);
}

/// <summary>Whether a clone into WSL can be attempted on this machine, and why not when it cannot.</summary>
/// <param name="Available">True when wsl.exe exists, a default distribution starts, and git runs inside it.</param>
/// <param name="Detail">The git version when available; the reason otherwise (shown to the user).</param>
public sealed record WslAvailability(bool Available, string Detail);

/// <summary>Where a repository ended up after a clone into WSL.</summary>
/// <param name="Distribution">The WSL distribution that holds the clone (its default distribution).</param>
/// <param name="LinuxPath">Path inside the distribution, e.g. <c>/home/me/gclo/acme/repo</c>.</param>
/// <param name="WindowsPath">The same folder as Explorer reaches it: <c>\\wsl.localhost\&lt;distro&gt;\home\me\gclo\acme\repo</c>.</param>
public sealed record WslCloneResult(string Distribution, string LinuxPath, string WindowsPath);

/// <summary>A clone into WSL did not complete; the message carries git's or wsl.exe's own explanation.</summary>
public sealed class WslCloneException : Exception
{
    /// <param name="message">What failed, in git's or wsl.exe's words.</param>
    public WslCloneException(string message) : base(message) { }
}

/// <summary>
/// The "Clone in WSL instead" recovery for repositories whose trees contain
/// Windows-invalid paths (#8): a Linux file system has no such rule, so the
/// repository is cloned by the distribution's own git into
/// <c>~/gclo/&lt;organization&gt;/&lt;repository&gt;</c> and left there.
/// </summary>
public interface IWslCloner
{
    /// <summary>Checks, without side effects, whether a clone into WSL can be attempted.</summary>
    Task<WslAvailability> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clones <paramref name="url"/> inside the default WSL distribution, or fast-forwards
    /// it when that folder is already a repository. Throws <see cref="WslCloneException"/>
    /// when git or wsl.exe fail.
    /// </summary>
    /// <param name="url">HTTPS clone URL.</param>
    /// <param name="organization">Organization (or user) name; the parent folder under ~/gclo.</param>
    /// <param name="repositoryName">Repository name; the clone's folder name.</param>
    /// <param name="token">GitHub token; handed to git through the environment only, never on a command line.</param>
    /// <param name="cancellationToken">Kills wsl.exe (and the clone) when canceled.</param>
    Task<WslCloneResult> CloneAsync(string url, string organization, string repositoryName, string token, CancellationToken cancellationToken);
}

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
        if (!_fileExists(_wslExe))
        {
            return new WslAvailability(false, "wsl.exe is not installed (Windows Subsystem for Linux is not enabled).");
        }

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_wslExe, ["-e", "git", "--version"], null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WslAvailability(false, $"wsl.exe could not be started: {ex.Message}");
        }

        string stdout = result.StandardOutput.Trim();
        if (result.ExitCode == 0 && stdout.StartsWith("git version", StringComparison.Ordinal))
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WslCloneException($"wsl.exe could not be started: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            string reason = LastLine(result.StandardError) ?? LastLine(result.StandardOutput) ?? $"exit code {result.ExitCode}";
            throw new WslCloneException($"Clone in WSL failed: {reason}");
        }

        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || lines[^1].Length == 0 || lines[^2].Length == 0)
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

/// <summary>
/// The real <see cref="IProcessRunner"/>. Output is read as bytes and decoded as UTF-16
/// when it carries NULs (wsl.exe's own messages, e.g. "no distribution installed", are
/// UTF-16) and as UTF-8 otherwise (everything a Linux process prints).
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Spawns real processes; WslCloner is covered through a fake runner.")]
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = start };
        process.Start();

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        Task copyErr = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(copyOut, copyErr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }

        return new ProcessResult(process.ExitCode, Decode(stdout.ToArray()), Decode(stderr.ToArray()));
    }

    private static string Decode(byte[] bytes)
        => bytes.Length >= 2 && Array.IndexOf(bytes, (byte)0) >= 0
            ? Encoding.Unicode.GetString(bytes).Replace("\0", "")
            : Encoding.UTF8.GetString(bytes);
}
