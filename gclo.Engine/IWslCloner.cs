/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// The "Clone in WSL instead" recovery for repositories whose trees contain
/// Windows-invalid paths (#8): a Linux file system has no such rule, so the
/// repository is cloned by the distribution's own git into
/// <c>~/gclo/&lt;organization&gt;/&lt;repository&gt;</c> and left there.
/// </summary>
public interface IWslCloner
{
   /// <summary>Checks, without side effects, whether a clone into WSL can be attempted.</summary>
   Task<WslAvailability> ProbeAsync(CancellationToken cancellationToken);



   /// <summary>
   /// Clones <paramref name="url"/> inside the default WSL distribution, or fast-forwards
   /// it when that folder is already a repository. Throws <see cref="WslCloneException"/>
   /// when git or wsl.exe fail.
   /// </summary>
   /// <param name="url">HTTPS clone URL.</param>
   /// <param name="organization">Organization (or user) name; the parent folder under ~/gclo.</param>
   /// <param name="repositoryName">Repository name; the clone's folder name.</param>
   /// <param name="token">GitHub token; handed to git through the environment only, never on a command line.</param>
   /// <param name="cancellationToken">Kills wsl.exe (and the clone) when canceled.</param>
   Task<WslCloneResult> CloneAsync(string url, string organization, string repositoryName, string token, CancellationToken cancellationToken);
}
