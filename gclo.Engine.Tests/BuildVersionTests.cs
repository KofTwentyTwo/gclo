using gclo.Engine;

namespace gclo.Engine.Tests;

/// <summary>Formatting of the version + git-hash build identity string.</summary>
public sealed class BuildVersionTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.1.0-dev", "0.1.0-dev")]
    [InlineData("0.1.0-beta.6+9ac6c2b1234567890abcdef", "0.1.0-beta.6 (9ac6c2b12)")]
    [InlineData("1.2.3+abc", "1.2.3 (abc)")]
    [InlineData("1.2.3+9ac6c2b1234567890.dirty", "1.2.3 (9ac6c2b12)")]
    [InlineData("1.2.3+", "1.2.3")]
    public void Format_ProducesReadableIdentity(string? informational, string expected)
    {
        Assert.Equal(expected, BuildVersion.Format(informational));
    }

    [Fact]
    public void Describe_UsesThisAssembly_AndNeverThrows()
    {
        string described = BuildVersion.Describe(typeof(BuildVersionTests).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(described));
    }
}
