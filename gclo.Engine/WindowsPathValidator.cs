using LibGit2Sharp;

namespace gclo.Engine;

/// <summary>
/// Validates every path in a git tree against Windows file-system rules before a
/// checkout is attempted, so failures surface as structured, actionable results
/// instead of raw libgit2 errors. Path *length* is not validated here: clones and
/// pulls run with core.longpaths=true, which lifts the 260-character limit; only
/// the per-segment limit (255) still applies.
/// </summary>
public static class WindowsPathValidator
{
    private const int MaxSegmentLength = 255;

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns every Windows-invalid path in <paramref name="tree"/>; empty when all
    /// are fine. Flattens the tree to its leaf paths and runs them through
    /// <see cref="ValidatePaths"/>, so both entry points share one implementation.
    /// </summary>
    public static IReadOnlyList<InvalidPathInfo> Validate(Tree tree)
        => ValidatePaths(FlattenPaths(tree));

    /// <summary>
    /// Depth-first flattening of <paramref name="tree"/> into leaf paths (blobs and
    /// submodule links). An empty subtree is emitted as its own path so its name is
    /// still validated.
    /// </summary>
    private static IEnumerable<string> FlattenPaths(Tree tree)
    {
        var stack = new Stack<(Tree Tree, string Prefix)>();
        stack.Push((tree, ""));

        while (stack.Count > 0)
        {
            var (current, prefix) = stack.Pop();
            foreach (var entry in current)
            {
                string repoPath = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
                if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    var subtree = (Tree)entry.Target;
                    if (subtree.Count == 0)
                    {
                        yield return repoPath;
                    }
                    else
                    {
                        stack.Push((subtree, repoPath));
                    }
                }
                else
                {
                    yield return repoPath;
                }
            }
        }
    }

    /// <summary>
    /// Validates a flat set of file paths (forward-slash separated) against Windows
    /// rules — the single validation implementation, which <see cref="Validate(Tree)"/>
    /// feeds by flattening a git tree. Also used directly for the EFFECTIVE path set
    /// after a <see cref="PathRecovery"/> mapping is applied, where — unlike in a git
    /// tree — two source paths can also land on the same destination; those collisions
    /// (duplicate destinations, file/directory clashes) are reported as invalid too.
    /// </summary>
    public static IReadOnlyList<InvalidPathInfo> ValidatePaths(IEnumerable<string> filePaths)
    {
        var invalid = new List<InvalidPathInfo>();
        // Lowered path -> first-seen original casing, for case-only collisions.
        var seenCi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Exact-cased prefixes already segment-checked (shared directories repeat per file).
        var seenExact = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in filePaths)
        {
            string[] segments = path.Split('/');
            string prefix = "";
            for (int i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[i] : $"{prefix}/{segments[i]}";
                bool isLeaf = i == segments.Length - 1;

                if (isLeaf)
                {
                    if (!files.Add(path))
                    {
                        invalid.Add(new InvalidPathInfo(path, "more than one path maps to this destination", null));
                        break; // an exact duplicate needs no further checks
                    }
                    if (directories.Contains(path))
                    {
                        invalid.Add(new InvalidPathInfo(path, "maps to both a file and a directory", null));
                    }
                }
                else if (directories.Add(prefix) && files.Contains(prefix))
                {
                    invalid.Add(new InvalidPathInfo(prefix, "maps to both a file and a directory", null));
                }

                if (seenExact.Add(prefix))
                {
                    if (ValidateSegment(segments[i]) is { } problem)
                    {
                        invalid.Add(new InvalidPathInfo(prefix, problem.Reason, problem.Suggestion));
                    }
                    if (seenCi.TryGetValue(prefix, out string? original))
                    {
                        invalid.Add(new InvalidPathInfo(prefix, $"differs only by case from '{original}'", null));
                    }
                    else
                    {
                        seenCi[prefix] = prefix;
                    }
                }
            }
        }

        return invalid;
    }

    private static (string Reason, string? Suggestion)? ValidateSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return ("empty path segment", "_");
        }
        if (segment.Length > MaxSegmentLength)
        {
            return ($"segment longer than {MaxSegmentLength} characters", segment[..MaxSegmentLength]);
        }
        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            var trimmed = segment.TrimEnd(' ', '.');
            return ("trailing space or dot", trimmed.Length == 0 ? "_" : trimmed);
        }
        if (segment.IndexOfAny(InvalidChars) >= 0)
        {
            var sanitized = new string(segment.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray());
            return ("contains a character that is invalid on Windows", sanitized);
        }

        string baseName = segment.Split('.')[0];
        if (ReservedNames.Contains(baseName))
        {
            return ($"'{baseName}' is a reserved Windows device name", segment + "_");
        }

        return null;
    }
}
