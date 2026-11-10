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
   /// Writes the working tree of a repository whose paths Windows cannot create, with
   /// <paramref name="recovery"/>'s renames and skips applied. It applies to a
   /// repository that was fetched but never checked out (its clone failed with
   /// <see cref="InvalidRepositoryPathsException"/>, or it was empty) and to one an
   /// earlier call already wrote this way, for which it also brings in origin's new
   /// commits. Any previously persisted recovery is merged in first, the newer choice
   /// for a path winning, so a repository that gains new invalid paths can be fixed
   /// without restating earlier choices.
   /// </summary>
   /// <remarks>
   /// The tree is written by hand, with force, so this refuses
   /// (<see cref="InvalidOperationException"/>) a clone that was checked out
   /// normally: that tree may hold someone's work. It moves a branch only to the
   /// tip of the branch it tracks, and off the current commit only when that is
   /// contained in that tip or was itself once origin's tip. Throws
   /// <see cref="InvalidRepositoryPathsException"/> when the effective path set is
   /// still invalid or collides, before anything is changed; on success the merged
   /// recovery is persisted so later pulls re-apply it to new commits.
   /// </remarks>
   Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken);
}
