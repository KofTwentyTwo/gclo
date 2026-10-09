using gclo.Engine;
using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Pins the activity-log contract of #40: every user- or app-initiated action in the
/// view-model layer writes a human-readable entry, and no entry ever contains a token.
/// (Shell-level actions — dialogs opened, update flow, settings diff — live in the UI
/// project and are covered by inspection; the log calls there are one-liners.)
/// </summary>
public sealed class ActionLoggingTests : IDisposable
{
    private const string Token = "ghp_SECRET_TOKEN_VALUE_1234";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingActivityLog _log = new();
    private readonly FakeRepositoryLister _lister = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeOrganizationLister _orgs = new();

    public void Dispose() => TryDeleteDirectory(_root);

    private sealed class SyncProgress(Action<RepoProgress> handler) : IProgress<RepoProgress>
    {
        public void Report(RepoProgress value) => handler(value);
    }

    private WorkspaceViewModel CreateViewModel()
        => new(_lister, _git, _orgs, handler => new SyncProgress(handler), TimeSpan.FromMilliseconds(1), _log);

    private void AssertNoTokenLogged()
        => Assert.All(_log.Messages, m => Assert.DoesNotContain(Token, m));

    // ---------------------------------------------------------------- workspace

    [Fact]
    public async Task Workspace_TokenEntry_AndOrganizationLookup_AreLogged_WithoutTheToken()
    {
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(["me", "acme"]);
        var vm = CreateViewModel();

        vm.Token = Token;
        await WaitUntilAsync(() => vm.Organizations.Count == 2, "lookup to finish");

        Assert.Contains(_log.Messages, m => m == $"Token entered in workspace 'Quick Sync' ({Token.Length} characters).");
        Assert.Contains("Organization lookup started.", _log.Messages);
        Assert.Contains("Organization lookup finished: 2 organizations and accounts visible.", _log.Messages);
        AssertNoTokenLogged();

        vm.Token = "";
        Assert.Contains("Token cleared in workspace 'Quick Sync'.", _log.Messages);
    }

    [Fact]
    public async Task Workspace_OrganizationLookupFailure_IsLoggedAsAnError()
    {
        _orgs.Handler = (_, _) => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("401"));
        var vm = CreateViewModel();

        vm.Token = Token;
        await WaitUntilAsync(() => vm.LoadErrorOpen, "lookup failure to surface");

        Assert.Contains(_log.Entries, e => e.Level == "ERROR" && e.Message == "Organization lookup failed: 401");
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Workspace_RetryFailed_PathRecovery_AndFolderOpened_AreLogged()
    {
        _lister.Repositories = Repos("alpha");
        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = Token;
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
        vm.TargetFolder = _root;
        await vm.LoadReposCommand.ExecuteAsync(null);
        var invalid = new List<InvalidPathInfo> { new("bad:name.txt", "invalid on Windows", "bad_name.txt") };
        _git.CloneHandler = (_, _, _, _, _) => Task.FromException(new InvalidRepositoryPathsException(invalid));
        await vm.SyncCommand.ExecuteAsync(null);
        Assert.True(vm.RetryFailedCommand.CanExecute(null));

        await vm.RetryFailedCommand.ExecuteAsync(null);
        Assert.Contains("Retrying 1 failed repositories.", _log.Messages);

        // The user opens recovery and cancels it.
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(null);
        await vm.ResolvePathsCommand.ExecuteAsync(vm.Repos[0]);
        Assert.Contains("alpha: path recovery requested (1 invalid paths).", _log.Messages);
        Assert.Contains("alpha: path recovery canceled; the repository stays failed.", _log.Messages);

        vm.NoteFolderOpened();
        Assert.Contains($"Opened folder '{vm.EffectiveTargetRoot}'.", _log.Messages);
        AssertNoTokenLogged();
    }

    // ---------------------------------------------------------------- accounts

    [Fact]
    public void AccountsStore_SaveUpdateDelete_AndSyncResult_AreLogged_WithoutTheToken()
    {
        var store = new AccountsStore(new InMemoryVault(), _root, _log);
        var account = new Account { Id = Guid.NewGuid(), Name = "Work", Organization = "acme", TargetRoot = @"C:\r" };

        store.Save(account, Token);
        Assert.Contains("Account 'Work' added with a new token.", _log.Messages);

        store.Save(account with { Description = "edited" }, null);
        Assert.Contains("Account 'Work' updated (token unchanged).", _log.Messages);

        store.RecordSyncResult(account.Id, DateTimeOffset.UtcNow, "1 cloned");
        Assert.Contains("Account 'Work': sync result recorded (1 cloned).", _log.Messages);

        store.Delete(account.Id);
        Assert.Contains("Account 'Work' deleted along with its token.", _log.Messages);

        var tokenless = new Account { Id = Guid.NewGuid(), Name = "Bare", Organization = "acme", TargetRoot = @"C:\r" };
        store.Save(tokenless, null);
        store.Delete(tokenless.Id);
        Assert.Contains("Account 'Bare' deleted (it had no token).", _log.Messages);
        AssertNoTokenLogged();
    }

    // ---------------------------------------------------------------- wizard

    [Fact]
    public async Task Wizard_TokenValidation_IsLogged_WithoutTheToken()
    {
        var store = new AccountsStore(new InMemoryVault(), _root, _log);
        var defaults = new AppSettings();
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(["me", "acme", "labs"]);
        var wizard = new AccountWizardViewModel(store, _orgs, defaults, log: _log) { Name = "Work", Token = Token };
        Assert.True(await wizard.TryAdvanceAsync());

        Assert.True(await wizard.TryAdvanceAsync());
        Assert.Contains("Account wizard: validating the token.", _log.Messages);
        Assert.Contains("Account wizard: token accepted; 3 organizations and accounts visible.", _log.Messages);

        _orgs.Handler = (_, _) => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("nope"));
        var rejected = new AccountWizardViewModel(store, _orgs, new AccountWizardSeed(Token, "acme", @"C:\r", false, 4), _log) { Name = "Other" };
        Assert.True(await rejected.TryAdvanceAsync());
        Assert.False(await rejected.TryAdvanceAsync());
        Assert.Contains(_log.Entries, e => e.Level == "ERROR" && e.Message == "Account wizard: token rejected: nope");
        AssertNoTokenLogged();
    }

    // ---------------------------------------------------------------- sync all

    [Fact]
    public async Task SyncAll_QueueIsLogged()
    {
        var coordinator = new SyncAllCoordinator(_log);
        var a = CreateViewModel();
        var b = CreateViewModel();

        await coordinator.RunAsync([a, b], CancellationToken.None);

        Assert.Contains("Sync all: queued 2 accounts: 'Quick Sync', 'Quick Sync'.", _log.Messages);
    }
}
