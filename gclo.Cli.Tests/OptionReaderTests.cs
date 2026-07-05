namespace gclo.Cli.Tests;

/// <summary>Covers the shared argument reader: both value forms and the flag/value guards.</summary>
public sealed class OptionReaderTests
{
    [Fact]
    public void MoveNext_SplitsInlineValue_AndStripsItFromCurrent()
    {
        var reader = new OptionReader(["--org=acme"]);

        Assert.True(reader.MoveNext());
        Assert.Equal("--org", reader.Current);
        Assert.Equal("acme", reader.RequireValue());
    }

    [Fact]
    public void MoveNext_PlainToken_KeepsItWhole()
    {
        var reader = new OptionReader(["sync"]);

        Assert.True(reader.MoveNext());
        Assert.Equal("sync", reader.Current);
    }

    [Fact]
    public void MoveNext_PastEnd_ReturnsFalse()
    {
        var reader = new OptionReader([]);

        Assert.False(reader.MoveNext());
    }

    [Fact]
    public void RequireValue_TakesTheFollowingArgument()
    {
        var reader = new OptionReader(["--org", "acme"]);
        reader.MoveNext();

        Assert.Equal("acme", reader.RequireValue());
    }

    [Fact]
    public void RequireValue_EmptyInlineValue_Throws()
    {
        var reader = new OptionReader(["--org="]);
        reader.MoveNext();

        Assert.Throws<CliUsageException>(() => reader.RequireValue());
    }

    [Fact]
    public void RequireValue_AtEnd_Throws()
    {
        var reader = new OptionReader(["--org"]);
        reader.MoveNext();

        Assert.Throws<CliUsageException>(() => reader.RequireValue());
    }

    [Fact]
    public void RequireValue_FollowedByAnotherOption_Throws()
    {
        var reader = new OptionReader(["--org", "--target"]);
        reader.MoveNext();

        Assert.Throws<CliUsageException>(() => reader.RequireValue());
    }

    [Fact]
    public void RejectValue_InlineValueOnAFlag_Throws()
    {
        var reader = new OptionReader(["--json=true"]);
        reader.MoveNext();

        Assert.Throws<CliUsageException>(() => reader.RejectValue());
    }

    [Fact]
    public void RejectValue_NoInlineValue_IsFine()
    {
        var reader = new OptionReader(["--json"]);
        reader.MoveNext();

        reader.RejectValue(); // must not throw
    }
}
