using gclo.Engine;

namespace gclo.Cli.Tests;

/// <summary>Pins the '--include' / '--exclude' / '--skip-archived' selection rules.</summary>
public sealed class RepoFilterSpecTests
{
    private static RepoDescriptor Repo(string name, bool archived = false)
        => new(name, $"https://x/{name}.git", "main", archived);

    [Fact]
    public void Empty_SelectsEverything()
    {
        var spec = new RepoFilterSpec();

        Assert.True(spec.IsEmpty);
        Assert.True(spec.Matches(Repo("anything")));
        Assert.True(spec.Matches(Repo("archived", archived: true)));
        Assert.Equal("none", spec.ToString());
    }

    [Theory]
    [InlineData("platform-*", "platform-api", true)]
    [InlineData("platform-*", "PLATFORM-WEB", true)] // case-insensitive
    [InlineData("platform-*", "docs", false)]
    [InlineData("*-api", "platform-api", true)]
    [InlineData("api", "platform-api", false)] // whole-name match, not substring
    [InlineData("repo-?", "repo-1", true)]
    [InlineData("repo-?", "repo-10", false)]
    [InlineData("a.b", "axb", false)] // '.' is literal
    public void Include_MatchesTheWholeName_WithGlobWildcards(string glob, string name, bool expected)
    {
        var spec = new RepoFilterSpec();
        spec.Include(glob);

        Assert.Equal(expected, spec.Matches(Repo(name)));
    }

    [Fact]
    public void MultipleIncludes_AreOrEd_AndExcludesWinOverIncludes()
    {
        var spec = new RepoFilterSpec();
        spec.Include("platform-*");
        spec.Include("docs");
        spec.Exclude("*-legacy");

        Assert.True(spec.Matches(Repo("platform-api")));
        Assert.True(spec.Matches(Repo("docs")));
        Assert.False(spec.Matches(Repo("platform-legacy")));
        Assert.False(spec.Matches(Repo("other")));
        Assert.Equal("include=2, exclude=1", spec.ToString());
    }

    [Fact]
    public void SkipArchived_DropsArchivedRepositories()
    {
        var spec = new RepoFilterSpec { SkipArchived = true };

        Assert.False(spec.IsEmpty);
        Assert.True(spec.Matches(Repo("live")));
        Assert.False(spec.Matches(Repo("old", archived: true)));
        Assert.Equal("skipArchived", spec.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPattern_IsAUsageError(string glob)
    {
        var spec = new RepoFilterSpec();

        var ex = Assert.Throws<CliUsageException>(() => spec.Include(glob));
        Assert.Contains("--include expects", ex.Message);
        Assert.Throws<CliUsageException>(() => spec.Exclude(glob));
    }
}
