namespace gclo.Cli.Tests;

/// <summary>
/// Covers token-source parsing and resolution: environment, file, and redirected
/// stdin, plus the multi-source and missing-token errors. The interactive no-echo
/// read is excluded from coverage (it needs a real terminal).
/// </summary>
public sealed class TokenOptionsTests
{
    private static TokenOptions Parse(params string[] args)
    {
        var options = new TokenOptions();
        var reader = new OptionReader(args);
        while (reader.MoveNext())
        {
            Assert.True(options.TryConsume(reader), $"expected a token option, got '{reader.Current}'");
        }
        return options;
    }

    [Fact]
    public void TryConsume_NonTokenOption_ReturnsFalse()
    {
        var reader = new OptionReader(["--json"]);
        reader.MoveNext();

        Assert.False(new TokenOptions().TryConsume(reader));
    }

    [Fact]
    public void HasExplicitSource_TrueOnlyAfterATokenOption()
    {
        Assert.False(new TokenOptions().HasExplicitSource);
        Assert.True(Parse("--token-env", "X").HasExplicitSource);
        Assert.True(Parse("--token-stdin").HasExplicitSource);
    }

    [Fact]
    public void Resolve_MoreThanOneSource_Throws()
    {
        var options = Parse("--token-env", "A", "--token-file", "b.txt");

        Assert.Throws<CliUsageException>(() => options.Resolve());
    }

    [Fact]
    public void Resolve_ExplicitEnvVar_ReturnsTrimmedValue()
    {
        const string var = "GCLO_TEST_TOKEN_ENV";
        Environment.SetEnvironmentVariable(var, "  ghp_from_env  ");
        try
        {
            Assert.Equal("ghp_from_env", Parse("--token-env", var).Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(var, null);
        }
    }

    [Fact]
    public void Resolve_ExplicitEnvVar_Missing_Throws()
    {
        var ex = Assert.Throws<CliErrorException>(
            () => Parse("--token-env", "GCLO_DEFINITELY_UNSET_VAR").Resolve());
        Assert.Contains("not set", ex.Message);
    }

    [Fact]
    public void Resolve_DefaultEnvVar_WhenUnset_ThrowsWithGuidance()
    {
        string? original = Environment.GetEnvironmentVariable(TokenOptions.DefaultVariable);
        Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, null);
        try
        {
            var ex = Assert.Throws<CliErrorException>(() => new TokenOptions().Resolve());
            Assert.Contains("--token-env", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, original);
        }
    }

    [Fact]
    public void Resolve_File_ReturnsFirstNonBlankLine()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "\n   \nghp_from_file\nsecond\n");
            Assert.Equal("ghp_from_file", Parse("--token-file", file).Resolve());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolve_File_AllBlank_Throws()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "\n   \n\t\n");
            var ex = Assert.Throws<CliErrorException>(() => Parse("--token-file", file).Resolve());
            Assert.Contains("empty", ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolve_File_Unreadable_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gclo-no-such-" + Guid.NewGuid().ToString("N") + ".txt");

        var ex = Assert.Throws<CliErrorException>(() => Parse("--token-file", missing).Resolve());
        Assert.Contains("Cannot read token file", ex.Message);
    }

    [Fact]
    public void Resolve_Stdin_ReturnsTheLine()
    {
        // The test host's stdin is redirected, so Resolve reads Console.In.
        using var _ = new StdinRedirect("  ghp_from_stdin  \n");

        Assert.Equal("ghp_from_stdin", Parse("--token-stdin").Resolve());
    }

    [Fact]
    public void Resolve_Stdin_Empty_Throws()
    {
        using var _ = new StdinRedirect("");

        var ex = Assert.Throws<CliErrorException>(() => Parse("--token-stdin").Resolve());
        Assert.Contains("standard input", ex.Message);
    }

    [Fact]
    public void TryConsume_StdinWithInlineValue_Throws()
    {
        var reader = new OptionReader(["--token-stdin=oops"]);
        reader.MoveNext();

        Assert.Throws<CliUsageException>(() => new TokenOptions().TryConsume(reader));
    }
}
