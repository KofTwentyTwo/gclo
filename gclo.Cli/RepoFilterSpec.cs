using System.Text.RegularExpressions;
using gclo.Engine;

namespace gclo.Cli;

/// <summary>
/// The repository selection shared by 'gclo sync' and 'gclo repos': any number of
/// '--include' and '--exclude' name globs ('*' and '?', case-insensitive, matched
/// against the whole name) plus '--skip-archived'. A repository is selected when it
/// matches at least one include (or there are none), matches no exclude, and is not
/// archived when archived ones are skipped — the same list-select-sync shape the
/// desktop app's table offers.
/// </summary>
internal sealed class RepoFilterSpec
{
    private readonly List<Regex> _include = new();
    private readonly List<Regex> _exclude = new();

    /// <summary>When true, archived repositories are dropped from the selection.</summary>
    public bool SkipArchived { get; set; }

    /// <summary>True when no filter was given: every listed repository is selected.</summary>
    public bool IsEmpty => _include.Count == 0 && _exclude.Count == 0 && !SkipArchived;

    /// <summary>Adds an '--include' glob.</summary>
    public void Include(string glob) => _include.Add(GlobToRegex(glob, "--include"));

    /// <summary>Adds an '--exclude' glob.</summary>
    public void Exclude(string glob) => _exclude.Add(GlobToRegex(glob, "--exclude"));

    /// <summary>Whether <paramref name="repo"/> is part of the selection.</summary>
    public bool Matches(RepoDescriptor repo)
        => (!SkipArchived || !repo.IsArchived)
            && (_include.Count == 0 || _include.Any(r => r.IsMatch(repo.Name)))
            && !_exclude.Any(r => r.IsMatch(repo.Name));

    /// <summary>Human-readable description for the activity log.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (_include.Count > 0)
        {
            parts.Add($"include={_include.Count}");
        }
        if (_exclude.Count > 0)
        {
            parts.Add($"exclude={_exclude.Count}");
        }
        if (SkipArchived)
        {
            parts.Add("skipArchived");
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    internal static Regex GlobToRegex(string glob, string option)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            throw new CliUsageException($"{option} expects a repository name pattern such as 'platform-*'.");
        }
        string pattern = "^" + Regex.Escape(glob.Trim()).Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
