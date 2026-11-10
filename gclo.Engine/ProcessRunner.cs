/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;


namespace gclo.Engine;


/// <summary>
/// The real <see cref="IProcessRunner"/>. Output is read as bytes and decoded as UTF-16
/// when it carries NULs (wsl.exe's own messages, e.g. "no distribution installed", are
/// UTF-16) and as UTF-8 otherwise (everything a Linux process prints).
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Spawns real processes; WslCloner is covered through a fake runner.")]
public sealed class ProcessRunner : IProcessRunner
{
   /// <inheritdoc />
   public async Task<ProcessResult> RunAsync(
       string fileName,
       IReadOnlyList<string> arguments,
       IReadOnlyDictionary<string, string>? environment,
       string? standardInput,
       CancellationToken cancellationToken)
   {
      var start = new ProcessStartInfo(fileName)
      {
         UseShellExecute = false,
         CreateNoWindow = true,
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
      };
      foreach(string argument in arguments)
      {
         start.ArgumentList.Add(argument);
      }
      if(environment is not null)
      {
         foreach(KeyValuePair<string, string> pair in environment)
         {
            start.Environment[pair.Key] = pair.Value;
         }
      }

      using var process = new Process { StartInfo = start };
      // Declared before the child is started, so they are released after it has been
      // ended: what still copies into them on a failure is not cut off under it.
      using var stdout = new MemoryStream();
      using var stderr = new MemoryStream();
      process.Start();

      ///////////////////////////////////////////////////////////////////////////////
      // From here on the child exists, so every way out of this method, not only  //
      // cancellation, goes through the finally that ends it if it is still alive. //
      ///////////////////////////////////////////////////////////////////////////////
      try
      {
         Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
         Task copyErr = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);
         try
         {
            if(standardInput is not null)
            {
               await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            process.StandardInput.Close();
         }
         catch(IOException)
         {
            ////////////////////////////////////////////////////////////////////////
            // The child closed its input, or exited, before it had read all of   //
            // it. That is its answer, not a failure to run it: its exit code and  //
            // what it printed say why, and they are what the caller gets.         //
            ////////////////////////////////////////////////////////////////////////
         }

         await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
         await Task.WhenAll(copyOut, copyErr).ConfigureAwait(false);
         return new ProcessResult(process.ExitCode, Decode(stdout.ToArray()), Decode(stderr.ToArray()));
      }
      finally
      {
         EndIfStillRunning(process);
      }
   }



   private static void EndIfStillRunning(Process process)
   {
      try
      {
         if(!process.HasExited)
         {
            process.Kill(entireProcessTree: true);
         }
      }
      catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                   or NotSupportedException or AggregateException)
      {
         // It exited in between, or it (or one of its children) cannot be ended from here.
      }
   }



   private static string Decode(byte[] bytes)
       => bytes.Length >= 2 && Array.IndexOf(bytes, (byte)0) >= 0
           ? Encoding.Unicode.GetString(bytes).Replace("\0", "")
           : Encoding.UTF8.GetString(bytes);
}
