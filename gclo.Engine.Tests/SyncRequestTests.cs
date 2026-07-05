namespace gclo.Engine.Tests;

/// <summary>
/// Pins the security contract of <see cref="SyncRequest"/>: the record ships on
/// NuGet, and its ToString must never print the PAT (the generated record
/// ToString would include every positional member).
/// </summary>
public sealed class SyncRequestTests
{
    [Fact]
    public void ToString_NeverContainsTheToken()
    {
        var request = new SyncRequest("acme", "ghp_super_secret_token", @"C:\src", 8);

        string text = request.ToString();

        Assert.DoesNotContain("ghp_super_secret_token", text);
        Assert.Contains("[redacted]", text);
        Assert.Contains("acme", text); // everything else stays diagnosable
    }
}
