/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Git transport operations for a single repository.</summary>
public interface IGitClient
{
   /// <summary>True when <paramref name="path"/> is an existing local git repository.</summary>
   bool IsValidRepository(string path);



   /// <summary>Clones <paramref name="url"/> into <paramref name="path"/>.</summary>
   /// <param name="url">HTTPS clone URL.</param>
   /// <param name="path">Local directory the repository is cloned into.</param>
   /// <param name="token">GitHub Personal Access Token used for git transport.</param>
   /// <param name="onProgress">Receives object-transfer completion in [0,1], when known.</param>
   /// <param name="cancellationToken">Cancels the clone.</param>
   Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken);



   /// <summary>
   /// Fetches <paramref name="url"/> into the repository at <paramref name="path"/> and
   /// fast-forwards the current branch to its upstream. The folder's <c>origin</c> must
   /// name the same repository as <paramref name="url"/> (scheme, credentials and a
   /// trailing <c>.git</c> aside); a folder that is a clone of something else fails
   /// without being fetched, so a sync never reports a different repository as updated.
   /// The token is offered only over HTTPS to the host of <paramref name="url"/>.
   /// </summary>
   /// <param name="url">The clone URL the repository was listed with.</param>
   /// <param name="path">Local directory of the existing clone.</param>
   /// <param name="token">Access token used for git transport.</param>
   /// <param name="cancellationToken">Cancels the pull.</param>
   Task FetchAndPullAsync(string url, string path, string token, CancellationToken cancellationToken);



   /// <summary>
   /// Materializes the working tree of a fetched-but-never-checked-out repository
   /// (one whose clone failed with <see cref="InvalidRepositoryPathsException"/>)
   /// by writing HEAD's blobs to disk with <paramref name="recovery"/>'s renames
   /// and skips applied. Any previously persisted recovery is merged in first,
   /// with <paramref name="recovery"/> winning on conflicts, so a repository that
   /// gains new invalid paths can be fixed without restating earlier choices.
   /// Throws <see cref="InvalidRepositoryPathsException"/> when the effective
   /// (post-mapping) path set is still invalid or collides; on success the merged
   /// recovery is persisted so later pulls re-apply it to new commits.
   /// </summary>
   Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken);
}
