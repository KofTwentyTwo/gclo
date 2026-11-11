/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Lists every repository of an organization or account on a forge (GitHub, a Gitea server).</summary>
public interface IRepositoryLister
{
   /// <summary>Returns all repositories in the organization, following pagination to the end.</summary>
   /// <exception cref="ForgeAccessException">The forge refused the request, or could not be reached.</exception>
   Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
       string organization, string token, CancellationToken cancellationToken = default);
}
