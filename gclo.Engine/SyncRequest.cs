namespace gclo.Engine;

/// <summary>Inputs for an organization sync.</summary>
/// <param name="Organization">GitHub organization login name.</param>
/// <param name="Token">GitHub Personal Access Token used for both the API and git transport.</param>
/// <param name="TargetRoot">Local folder under which each repository is placed as a subfolder.</param>
/// <param name="MaxConcurrency">Upper bound on simultaneous git operations.</param>
public sealed record SyncRequest(string Organization, string Token, string TargetRoot, int MaxConcurrency = 8)
{
    /// <summary>
    /// Redacts the token: the record-generated ToString would print every positional
    /// member, and this type ships publicly on NuGet where any log or exception
    /// interpolation of a request would otherwise leak the PAT.
    /// </summary>
    public override string ToString() =>
        $"SyncRequest {{ Organization = {Organization}, Token = [redacted], "
        + $"TargetRoot = {TargetRoot}, MaxConcurrency = {MaxConcurrency} }}";
}
