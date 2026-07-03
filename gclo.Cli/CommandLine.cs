namespace gclo.Cli;

/// <summary>
/// A command-line usage error. The message is printed to stderr followed by a
/// help hint, and the process exits with code 2.
/// </summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// A fatal runtime error (missing token, rejected token, organization not found,
/// unusable target folder). The message is printed to stderr and the process
/// exits with code 2.
/// </summary>
internal sealed class CliErrorException : Exception
{
    public CliErrorException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Sequential reader over a command's arguments supporting both
/// '--name value' and '--name=value' forms.
/// </summary>
internal sealed class OptionReader
{
    private readonly string[] _args;
    private int _index;
    private string? _inlineValue;

    public OptionReader(string[] args) => _args = args;

    /// <summary>The current token, without any inline '=value' part.</summary>
    public string Current { get; private set; } = "";

    public bool MoveNext()
    {
        _inlineValue = null;
        if (_index >= _args.Length)
        {
            return false;
        }

        string token = _args[_index++];
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                _inlineValue = token[(equals + 1)..];
                token = token[..equals];
            }
        }
        Current = token;
        return true;
    }

    /// <summary>
    /// The value for <see cref="Current"/>: its inline '=value' part or the next
    /// argument. A following token that itself starts with '--' is treated as a
    /// missing value (use '--name=--literal' to pass such a value literally).
    /// </summary>
    public string RequireValue()
    {
        if (_inlineValue is not null)
        {
            if (_inlineValue.Length == 0)
            {
                throw new CliUsageException($"Option '{Current}' requires a value.");
            }
            return _inlineValue;
        }

        if (_index >= _args.Length || _args[_index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"Option '{Current}' requires a value.");
        }
        return _args[_index++];
    }

    /// <summary>Rejects '--name=value' written for a flag that takes no value.</summary>
    public void RejectValue()
    {
        if (_inlineValue is not null)
        {
            throw new CliUsageException($"Option '{Current}' does not take a value.");
        }
    }
}
