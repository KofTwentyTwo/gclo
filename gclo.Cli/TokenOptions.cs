namespace gclo.Cli;

/// <summary>
/// Collects and resolves the GitHub token source options shared by all commands.
/// There is deliberately no plain '--token &lt;value&gt;' option: process command
/// lines are visible to other processes on the machine (Task Manager, 'ps', WMI
/// queries), so a token passed as an argument would leak to every local user and
/// program. The supported sources — environment variable, file, stdin — never put
/// the secret on a command line.
/// </summary>
internal sealed class TokenOptions
{
    /// <summary>Environment variable consulted when no token option is given.</summary>
    public const string DefaultVariable = "GITHUB_TOKEN";

    private string? _envVariable;
    private string? _filePath;
    private bool _useStdin;

    /// <summary>
    /// True when any token option (--token-env, --token-file, --token-stdin) was
    /// given. 'gclo sync --account' uses this to let an explicit token source
    /// override the account's vault-stored token.
    /// </summary>
    public bool HasExplicitSource => _envVariable is not null || _filePath is not null || _useStdin;

    /// <summary>Consumes the reader's current option when it is a token option.</summary>
    public bool TryConsume(OptionReader reader)
    {
        switch (reader.Current)
        {
            case "--token-env":
                _envVariable = reader.RequireValue();
                return true;
            case "--token-file":
                _filePath = reader.RequireValue();
                return true;
            case "--token-stdin":
                reader.RejectValue();
                _useStdin = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the token from the single configured source, or from the
    /// <see cref="DefaultVariable"/> environment variable when none was given.
    /// Throws with an actionable message when the token is missing or empty.
    /// </summary>
    public string Resolve()
    {
        int sources = (_envVariable is null ? 0 : 1) + (_filePath is null ? 0 : 1) + (_useStdin ? 1 : 0);
        if (sources > 1)
        {
            throw new CliUsageException("Use only one of --token-env, --token-file, --token-stdin.");
        }

        if (_useStdin)
        {
            return FromStdin();
        }
        if (_filePath is not null)
        {
            return FromFile(_filePath);
        }
        return FromEnvironment(_envVariable ?? DefaultVariable, wasExplicit: _envVariable is not null);
    }

    private static string FromEnvironment(string variable, bool wasExplicit)
    {
        string? token = Environment.GetEnvironmentVariable(variable)?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new CliErrorException(wasExplicit
                ? $"Environment variable '{variable}' is not set or is empty."
                : $"No token source given and the default environment variable '{variable}' is not set. "
                  + "Provide a token with --token-env, --token-file, or --token-stdin.");
        }
        return token;
    }

    private static string FromFile(string path)
    {
        string? line;
        try
        {
            // First non-blank line: token files often end with a newline, and some
            // editors append trailing (or prepend leading) blank lines.
            line = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CliErrorException($"Cannot read token file '{path}': {ex.Message}", ex);
        }

        line = line?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            throw new CliErrorException($"Token file '{path}' is empty.");
        }
        return line;
    }

    private static string FromStdin()
    {
        string? line = Console.IsInputRedirected
            ? Console.In.ReadLine()?.Trim()
            : ReadInteractiveToken();

        if (string.IsNullOrEmpty(line))
        {
            throw new CliErrorException("No token arrived on standard input (expected one line).");
        }
        return line;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Interactive prompt + no-echo read; reachable only from a real terminal, not an offline test.")]
    private static string ReadInteractiveToken()
    {
        // Interactive use: prompt on stderr (redirected stdout stays clean) and read
        // WITHOUT echo, like git/gh credential prompts — an echoed token would sit on
        // screen and in the terminal scrollback.
        Console.Error.Write("Token: ");
        string line = ReadLineNoEcho().Trim();
        Console.Error.WriteLine();
        return line;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Interactive no-echo console read; keystrokes cannot be simulated in an offline test.")]
    private static string ReadLineNoEcho()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (key.KeyChar != '\0')
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
