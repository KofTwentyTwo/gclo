namespace gclo.Engine.Tests;

/// <summary>Pins the user-facing message of <see cref="InvalidRepositoryPathsException"/>.</summary>
public sealed class InvalidRepositoryPathsExceptionTests
{
    [Fact]
    public void Message_SinglePath_UsesSingularWording()
    {
        var ex = new InvalidRepositoryPathsException(
            [new InvalidPathInfo("aux", "'aux' is a reserved Windows device name", "aux_")]);

        Assert.StartsWith("1 path in this repository cannot be created on Windows:", ex.Message);
        Assert.Contains("'aux' ('aux' is a reserved Windows device name)", ex.Message);
        Assert.EndsWith("Nothing was checked out.", ex.Message);
    }

    [Fact]
    public void Message_ManyPaths_UsesPluralWording_AndCapsExamplesAtThree()
    {
        var paths = Enumerable.Range(1, 5)
            .Select(i => new InvalidPathInfo(
                $"docs/bad:{i}.txt", "contains a character that is invalid on Windows", null))
            .ToList();

        var ex = new InvalidRepositoryPathsException(paths);

        Assert.StartsWith("5 paths in this repository cannot be created on Windows:", ex.Message);
        Assert.Contains(" and 2 more", ex.Message); // examples cap at three
        Assert.DoesNotContain("bad:4", ex.Message);
        Assert.EndsWith("Nothing was checked out.", ex.Message);
    }
}
