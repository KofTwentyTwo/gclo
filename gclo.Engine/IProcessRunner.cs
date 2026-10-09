/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// Runs an external process to completion. The seam that keeps <see cref="WslCloner"/>
/// testable without a WSL installation: the offline suite scripts results through a fake.
/// </summary>
public interface IProcessRunner
{
   /// <param name="fileName">Executable path.</param>
   /// <param name="arguments">Argument vector (quoted by the runner, never by the caller).</param>
   /// <param name="environment">Extra environment variables for the child; the parent's are inherited.</param>
   /// <param name="standardInput">Text written to the child's stdin, then closed; null leaves stdin closed.</param>
   /// <param name="cancellationToken">Kills the child and throws <see cref="OperationCanceledException"/>.</param>
   Task<ProcessResult> RunAsync(
       string fileName,
       IReadOnlyList<string> arguments,
       IReadOnlyDictionary<string, string>? environment,
       string? standardInput,
       CancellationToken cancellationToken);
}
