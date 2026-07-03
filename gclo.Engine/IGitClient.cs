namespace gclo.Engine;

/// <summary>Git transport operations for a single repository.</summary>
public interface IGitClient
{
    /// <summary>True when <paramref name="path"/> is an existing local git repository.</summary>
    bool IsValidRepository(string path);

    /// <summary>Clones <paramref name="url"/> into <paramref name="path"/>.</summary>
    /// <param name="onProgress">Receives object-transfer completion in [0,1], when known.</param>
    Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken);

    /// <summary>Fetches from origin and fast-forwards the current branch to its upstream.</summary>
    Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken);
}
