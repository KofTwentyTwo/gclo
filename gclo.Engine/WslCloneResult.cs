/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Where a repository ended up after a clone into WSL.</summary>
/// <param name="Distribution">The WSL distribution that holds the clone (its default distribution).</param>
/// <param name="LinuxPath">Path inside the distribution, e.g. <c>/home/me/gclo/acme/repo</c>.</param>
/// <param name="WindowsPath">The same folder as Explorer reaches it: <c>\\wsl.localhost\&lt;distro&gt;\home\me\gclo\acme\repo</c>.</param>
public sealed record WslCloneResult(string Distribution, string LinuxPath, string WindowsPath);
