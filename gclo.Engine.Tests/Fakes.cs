/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;


namespace gclo.Engine.Tests;


/// <summary>An <see cref="IRepositoryLister"/> that returns a configurable list (or throws).</summary>
public sealed class FakeRepositoryLister : IRepositoryLister
{
   private readonly ConcurrentQueue<(string Organization, string Token)> _calls = new();

   public IReadOnlyList<RepoDescriptor> Repositories { get; set; } = Array.Empty<RepoDescriptor>();

   /// <summary>When set, <see cref="ListOrganizationRepositoriesAsync"/> throws this instead of returning.</summary>
   public Exception? ExceptionToThrow { get; set; }



   public IReadOnlyList<(string Organization, string Token)> Calls => _calls.ToArray();



   public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
       string organization, string token, CancellationToken cancellationToken = default)
   {
      _calls.Enqueue((organization, token));
      if(ExceptionToThrow is not null)
      {
         return Task.FromException<IReadOnlyList<RepoDescriptor>>(ExceptionToThrow);
      }
      return Task.FromResult(Repositories);
   }
}



public sealed record CloneCall(string Url, string LocalPath, string Token);



public sealed record PullCall(string LocalPath, string Token);



public sealed record ApplyRecoveryCall(string LocalPath, PathRecovery Recovery);



/// <summary>
/// An <see cref="IGitClient"/> whose behavior is configured with delegates.
/// All calls are recorded thread-safely; default handlers complete immediately.
/// </summary>
public sealed class FakeGitClient : IGitClient
{
   private readonly ConcurrentQueue<CloneCall> _cloneCalls = new();

   private readonly ConcurrentQueue<PullCall> _pullCalls = new();

   private readonly ConcurrentQueue<ApplyRecoveryCall> _applyRecoveryCalls = new();

   private readonly ConcurrentQueue<string> _validityChecks = new();

   /// <summary>Decides whether a local path counts as an existing valid repository. Default: never.</summary>
   public Func<string, bool> IsValidRepositoryHandler { get; set; } = _ => false;

   /// <summary>Body of <see cref="CloneAsync"/> (url, path, token, onProgress, ct). Default: completes immediately.</summary>
   public Func<string, string, string, Action<double>?, CancellationToken, Task> CloneHandler { get; set; }
       = (_, _, _, _, _) => Task.CompletedTask;

   /// <summary>Body of <see cref="FetchAndPullAsync"/> (path, token, ct). Default: completes immediately.</summary>
   public Func<string, string, CancellationToken, Task> FetchAndPullHandler { get; set; }
       = (_, _, _) => Task.CompletedTask;

   /// <summary>Body of <see cref="ApplyRecoveryAsync"/> (path, recovery, ct). Default: completes immediately.</summary>
   public Func<string, PathRecovery, CancellationToken, Task> ApplyRecoveryHandler { get; set; }
       = (_, _, _) => Task.CompletedTask;



   public IReadOnlyList<CloneCall> CloneCalls => _cloneCalls.ToArray();



   public IReadOnlyList<PullCall> PullCalls => _pullCalls.ToArray();



   public IReadOnlyList<ApplyRecoveryCall> ApplyRecoveryCalls => _applyRecoveryCalls.ToArray();



   public IReadOnlyList<string> ValidityChecks => _validityChecks.ToArray();



   /// <summary>Repo folder names (last path segment) passed to <see cref="CloneAsync"/>.</summary>
   public IReadOnlyList<string> ClonedRepoNames
       => _cloneCalls.Select(c => System.IO.Path.GetFileName(c.LocalPath)!).ToArray();



   /// <summary>Repo folder names (last path segment) passed to <see cref="FetchAndPullAsync"/>.</summary>
   public IReadOnlyList<string> PulledRepoNames
       => _pullCalls.Select(c => System.IO.Path.GetFileName(c.LocalPath)!).ToArray();



   public bool IsValidRepository(string path)
   {
      _validityChecks.Enqueue(path);
      return IsValidRepositoryHandler(path);
   }



   public async Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
   {
      _cloneCalls.Enqueue(new CloneCall(url, path, token));
      await CloneHandler(url, path, token, onProgress, cancellationToken).ConfigureAwait(false);
   }



   public async Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
   {
      _pullCalls.Enqueue(new PullCall(path, token));
      await FetchAndPullHandler(path, token, cancellationToken).ConfigureAwait(false);
   }



   public async Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
   {
      _applyRecoveryCalls.Enqueue(new ApplyRecoveryCall(path, recovery));
      await ApplyRecoveryHandler(path, recovery, cancellationToken).ConfigureAwait(false);
   }
}



/// <summary>
/// An <see cref="IActivityLog"/> that keeps every entry, for asserting that actions
/// are logged (#40) and that no entry ever carries a token.
/// </summary>
public sealed class RecordingActivityLog : IActivityLog
{
   private readonly ConcurrentQueue<(string Level, string Message)> _entries = new();



   public IReadOnlyList<(string Level, string Message)> Entries => _entries.ToArray();



   public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToArray();



   public void Info(string message) => _entries.Enqueue(("INFO", message));



   public void Error(string message, Exception? exception = null) => _entries.Enqueue(("ERROR", message));



   public string LogDirectory => "";



   public string CurrentLogFilePath => "";
}



/// <summary>An <see cref="IOrganizationLister"/> whose behavior is configured with a delegate.</summary>
public sealed class FakeOrganizationLister : IOrganizationLister
{
   private int _calls;

   public Func<string, CancellationToken, Task<IReadOnlyList<string>>> Handler { get; set; }
       = (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());



   public int Calls => _calls;



   public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
   {
      Interlocked.Increment(ref _calls);
      return Handler(token, cancellationToken);
   }
}



/// <summary>
/// Records every report synchronously into a thread-safe queue.
/// Deliberately NOT <see cref="Progress{T}"/>, which posts callbacks to a sync context
/// asynchronously and would make assertions racy.
/// </summary>
public sealed class RecordingProgress : IProgress<RepoProgress>
{
   private readonly ConcurrentQueue<RepoProgress> _reports = new();



   public void Report(RepoProgress value) => _reports.Enqueue(value);



   /// <summary>All reports in the order they were recorded.</summary>
   public IReadOnlyList<RepoProgress> Reports => _reports.ToArray();



   /// <summary>Ordered statuses reported for one repository.</summary>
   public IReadOnlyList<SyncStatus> StatusesFor(string repoName)
       => _reports.Where(r => string.Equals(r.RepoName, repoName, StringComparison.Ordinal)).Select(r => r.Status).ToArray();



   /// <summary>The most recent report for one repository, or null if none.</summary>
   public RepoProgress? LastFor(string repoName)
       => _reports.LastOrDefault(r => string.Equals(r.RepoName, repoName, StringComparison.Ordinal));
}



/// <summary>One scripted <see cref="IProcessRunner"/> invocation, as recorded by <see cref="FakeProcessRunner"/>.</summary>
public sealed record ProcessCall(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment,
    string? StandardInput);



/// <summary>An <see cref="IProcessRunner"/> that records calls and answers from a delegate.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
   private readonly ConcurrentQueue<ProcessCall> _calls = new();

   /// <summary>Answers each call; default: exit 0 with empty output.</summary>
   public Func<ProcessCall, CancellationToken, Task<ProcessResult>> Handler { get; set; }
       = (_, _) => Task.FromResult(new ProcessResult(0, "", ""));



   public IReadOnlyList<ProcessCall> Calls => _calls.ToArray();



   public Task<ProcessResult> RunAsync(
       string fileName,
       IReadOnlyList<string> arguments,
       IReadOnlyDictionary<string, string>? environment,
       string? standardInput,
       CancellationToken cancellationToken)
   {
      var call = new ProcessCall(fileName, arguments, environment, standardInput);
      _calls.Enqueue(call);
      return Handler(call, cancellationToken);
   }
}



public sealed record WslCloneCall(string Url, string Organization, string RepositoryName, string Token);



/// <summary>An <see cref="IWslCloner"/> with scripted availability and clone outcome.</summary>
public sealed class FakeWslCloner : IWslCloner
{
   private readonly ConcurrentQueue<WslCloneCall> _cloneCalls = new();

   private int _probes;

   /// <summary>What <see cref="ProbeAsync"/> reports; default: not available.</summary>
   public WslAvailability Availability { get; set; } = new(false, "wsl.exe is not installed (test default)");

   /// <summary>Body of <see cref="CloneAsync"/>; default: a successful clone into /home/me/gclo/org/repo.</summary>
   public Func<WslCloneCall, CancellationToken, Task<WslCloneResult>> CloneHandler { get; set; }
       = (call, _) => Task.FromResult(new WslCloneResult(
           "Ubuntu",
           $"/home/me/gclo/{call.Organization}/{call.RepositoryName}",
           $@"\\wsl.localhost\Ubuntu\home\me\gclo\{call.Organization}\{call.RepositoryName}"));



   public int Probes => _probes;



   public IReadOnlyList<WslCloneCall> CloneCalls => _cloneCalls.ToArray();



   public Task<WslAvailability> ProbeAsync(CancellationToken cancellationToken)
   {
      Interlocked.Increment(ref _probes);
      return Task.FromResult(Availability);
   }



   public Task<WslCloneResult> CloneAsync(string url, string organization, string repositoryName, string token, CancellationToken cancellationToken)
   {
      var call = new WslCloneCall(url, organization, repositoryName, token);
      _cloneCalls.Enqueue(call);
      return CloneHandler(call, cancellationToken);
   }
}
