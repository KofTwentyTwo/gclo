using gclo.ViewModels;

namespace gclo.Engine.Tests;

/// <summary>
/// Tests for <see cref="AccountWizardViewModel"/>: step gating, token validation,
/// defaults-vs-edit seeding, and save semantics. Every wizard runs against a real
/// <see cref="AccountsStore"/> in a unique temp directory, an <see cref="InMemoryVault"/>,
/// and a <see cref="FakeOrganizationLister"/>.
/// </summary>
public sealed class AccountWizardViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryVault _vault = new();
    private readonly FakeOrganizationLister _orgs = new();
    private readonly AccountsStore _store;
    private readonly AppSettings _defaults = new()
    {
        DefaultTargetFolder = @"C:\repos-default",
        DefaultMaxConcurrency = 6,
    };

    public AccountWizardViewModelTests()
    {
        _store = new AccountsStore(_vault, _root);
    }

    public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);

    private AccountWizardViewModel NewWizard(Account? existing = null, string? existingToken = null)
        => new(_store, _orgs, _defaults, existing, existingToken);

    private static Account MakeAccount(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Organization = "acme",
        TargetRoot = @"C:\repos",
    };

    /// <summary>Advances until the wizard sits on <paramref name="step"/>, asserting each hop.</summary>
    private static async Task AdvanceToStepAsync(AccountWizardViewModel wizard, int step)
    {
        while (wizard.Step < step)
        {
            int before = wizard.Step;
            Assert.True(await wizard.TryAdvanceAsync(), $"expected to advance past step {before}");
        }
        Assert.Equal(step, wizard.Step);
    }

    /// <summary>A new-account wizard walked to the last step with valid inputs everywhere.</summary>
    private async Task<AccountWizardViewModel> CreateWizardAtLastStepAsync()
    {
        var wizard = NewWizard();
        wizard.Name = "Work";
        wizard.Token = "ghp_token";
        await AdvanceToStepAsync(wizard, 3);
        wizard.Organization = "acme";
        await AdvanceToStepAsync(wizard, 4);
        return wizard;
    }

    // ---------------------------------------------------------------- seeding

    [Fact]
    public void NewWizard_StartsAtStepOne_SeededFromDefaults()
    {
        var wizard = NewWizard();

        Assert.Equal(1, wizard.Step);
        Assert.True(wizard.IsFirstStep);
        Assert.False(wizard.IsLastStep);
        Assert.False(wizard.IsEditing);
        Assert.Equal("Add account", wizard.Title);
        Assert.Equal("", wizard.Name);
        Assert.Equal("", wizard.Description);
        Assert.Equal("", wizard.Token);
        Assert.Equal("", wizard.Organization);
        Assert.Equal(@"C:\repos-default", wizard.TargetRoot);
        Assert.False(wizard.CreateOrgSubfolder);
        Assert.Equal(6, wizard.MaxConcurrency);
        Assert.Equal("", wizard.NameError);
        Assert.Equal("", wizard.TokenError);
    }

    [Fact]
    public void EditWizard_SeedsEveryFieldFromTheExistingAccount_AndItsToken()
    {
        var account = MakeAccount("Work") with
        {
            Description = "primary org",
            Organization = "acme-inc",
            TargetRoot = @"C:\work-repos",
            CreateOrgSubfolder = true,
            MaxConcurrency = 4,
        };

        var wizard = NewWizard(account, "ghp_original");

        Assert.True(wizard.IsEditing);
        Assert.Equal("Edit account", wizard.Title);
        Assert.Equal("Work", wizard.Name);
        Assert.Equal("primary org", wizard.Description);
        Assert.Equal("ghp_original", wizard.Token);
        Assert.Equal("acme-inc", wizard.Organization);
        Assert.Equal(@"C:\work-repos", wizard.TargetRoot);
        Assert.True(wizard.CreateOrgSubfolder);
        Assert.Equal(4, wizard.MaxConcurrency);
    }

    [Fact]
    public void EditWizard_WithNoVaultToken_SeedsAnEmptyTokenBox()
    {
        var wizard = NewWizard(MakeAccount("Work"), existingToken: null);

        Assert.Equal("", wizard.Token);
    }

    [Fact]
    public void MaxConcurrency_IsClampedToTheSettingsRange()
    {
        var wizard = NewWizard();

        wizard.MaxConcurrency = 0;
        Assert.Equal(AppSettings.MinConcurrency, wizard.MaxConcurrency);

        wizard.MaxConcurrency = 1000;
        Assert.Equal(AppSettings.MaxConcurrency, wizard.MaxConcurrency);
    }

    // ---------------------------------------------------------------- step 1: identity

    [Fact]
    public async Task TryAdvance_BlankName_SetsNameErrorAndStays()
    {
        var wizard = NewWizard();
        wizard.Name = "   ";

        Assert.False(await wizard.TryAdvanceAsync());

        Assert.Equal(1, wizard.Step);
        Assert.NotEqual("", wizard.NameError);
    }

    [Fact]
    public async Task TryAdvance_DuplicateName_SetsNameErrorAndStays_ThenClearsOnceFixed()
    {
        _store.Save(MakeAccount("Work"), null);
        var wizard = NewWizard();
        wizard.Name = "WORK"; // uniqueness is case-insensitive

        Assert.False(await wizard.TryAdvanceAsync());
        Assert.Equal(1, wizard.Step);
        Assert.Contains("Work", wizard.NameError, StringComparison.Ordinal);

        wizard.Name = "Personal";
        Assert.True(await wizard.TryAdvanceAsync());
        Assert.Equal(2, wizard.Step);
        Assert.Equal("", wizard.NameError);
    }

    [Fact]
    public async Task TryAdvance_EditKeepingItsOwnName_Advances()
    {
        var account = MakeAccount("Work");
        _store.Save(account, null);
        var wizard = NewWizard(account, "ghp_original");

        Assert.True(await wizard.TryAdvanceAsync());
        Assert.Equal(2, wizard.Step);
    }

    [Fact]
    public async Task TryAdvance_EditTakingAnotherAccountsName_IsBlocked()
    {
        var account = MakeAccount("Work");
        _store.Save(account, null);
        _store.Save(MakeAccount("Personal"), null);
        var wizard = NewWizard(account, "ghp_original");
        wizard.Name = "personal";

        Assert.False(await wizard.TryAdvanceAsync());
        Assert.Equal(1, wizard.Step);
        Assert.NotEqual("", wizard.NameError);
    }

    // ---------------------------------------------------------------- step 2: token

    [Fact]
    public async Task TryAdvance_AcceptedToken_FillsOrganizations_ClearsError_Advances()
    {
        string? validatedToken = null;
        _orgs.Handler = (token, _) =>
        {
            validatedToken = token;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "me", "acme" });
        };
        var wizard = NewWizard();
        wizard.Name = "Work";
        await AdvanceToStepAsync(wizard, 2);
        wizard.Token = "  ghp_valid  ";

        Assert.True(await wizard.TryAdvanceAsync());

        Assert.Equal(3, wizard.Step);
        Assert.Equal("ghp_valid", validatedToken); // validated as it will be saved: trimmed
        Assert.Equal(new[] { "me", "acme" }, wizard.Organizations);
        Assert.Equal("", wizard.TokenError);
        Assert.False(wizard.IsValidatingToken);
    }

    [Fact]
    public async Task TryAdvance_RejectedToken_SetsTokenErrorAndStays()
    {
        _orgs.Handler = (_, _) => throw new InvalidOperationException("GitHub rejected the token (401).");
        var wizard = NewWizard();
        wizard.Name = "Work";
        await AdvanceToStepAsync(wizard, 2);
        wizard.Token = "ghp_bad";

        Assert.False(await wizard.TryAdvanceAsync());

        Assert.Equal(2, wizard.Step);
        Assert.Equal("GitHub rejected the token (401).", wizard.TokenError);
        Assert.False(wizard.IsValidatingToken);
    }

    [Fact]
    public async Task IsValidatingToken_IsTrueExactlyWhileTheLookupIsInFlight()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _orgs.Handler = (_, _) => gate.Task;
        var wizard = NewWizard();
        wizard.Name = "Work";
        await AdvanceToStepAsync(wizard, 2);
        wizard.Token = "ghp_slow";

        Task<bool> advance = wizard.TryAdvanceAsync();
        Assert.True(wizard.IsValidatingToken);

        gate.SetResult(new[] { "me" });
        Assert.True(await advance);
        Assert.False(wizard.IsValidatingToken);
        Assert.Equal(3, wizard.Step);
    }

    // ---------------------------------------------------------------- steps 3 + 4

    [Fact]
    public async Task TryAdvance_BlankOrganization_StaysOnStepThree()
    {
        var wizard = NewWizard();
        wizard.Name = "Work";
        wizard.Token = "ghp_token";
        await AdvanceToStepAsync(wizard, 3);

        Assert.False(await wizard.TryAdvanceAsync());
        Assert.Equal(3, wizard.Step);

        wizard.Organization = "acme";
        Assert.True(await wizard.TryAdvanceAsync());
        Assert.Equal(4, wizard.Step);
        Assert.True(wizard.IsLastStep);
    }

    [Fact]
    public async Task TryAdvance_BlankTargetRoot_ReturnsFalseOnStepFour()
    {
        var wizard = await CreateWizardAtLastStepAsync();
        wizard.TargetRoot = "   ";

        Assert.False(await wizard.TryAdvanceAsync());
        Assert.Equal(4, wizard.Step);
    }

    [Fact]
    public async Task TryAdvance_ValidStepFour_ReturnsTrueWithoutAdvancing()
    {
        var wizard = await CreateWizardAtLastStepAsync();

        Assert.True(await wizard.TryAdvanceAsync());
        Assert.Equal(4, wizard.Step); // the host reacts by calling SaveAsync
    }

    [Fact]
    public async Task GoBack_StepsBackwards_AndIsANoOpOnStepOne()
    {
        var wizard = NewWizard();
        wizard.Name = "Work";
        wizard.Token = "ghp_token";
        await AdvanceToStepAsync(wizard, 2);

        wizard.GoBack();
        Assert.Equal(1, wizard.Step);
        Assert.True(wizard.IsFirstStep);

        wizard.GoBack();
        Assert.Equal(1, wizard.Step);
    }

    // ---------------------------------------------------------------- save: new accounts

    [Fact]
    public async Task SaveAsync_NewAccount_PersistsTheAccountAndPutsTheTokenInTheVault()
    {
        var wizard = await CreateWizardAtLastStepAsync();
        wizard.Description = "primary org";
        wizard.CreateOrgSubfolder = true;
        wizard.MaxConcurrency = 4;
        wizard.TargetRoot = @"C:\work-repos";

        await wizard.SaveAsync();

        var saved = Assert.Single(_store.GetAll());
        Assert.Equal("Work", saved.Name);
        Assert.Equal("primary org", saved.Description);
        Assert.Equal("acme", saved.Organization);
        Assert.Equal(@"C:\work-repos", saved.TargetRoot);
        Assert.True(saved.CreateOrgSubfolder);
        Assert.Equal(4, saved.MaxConcurrency);
        Assert.Null(saved.LastSyncUtc);
        Assert.Null(saved.LastSyncSummary);
        Assert.Equal("ghp_token", _vault.TryRetrieve(saved.Id));
    }

    [Fact]
    public async Task SaveAsync_TrimsEveryStringInput()
    {
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "acme" });
        var wizard = NewWizard();
        wizard.Name = "  Work  ";
        wizard.Description = "  primary org  ";
        wizard.Token = "  ghp_token  ";
        await AdvanceToStepAsync(wizard, 3);
        wizard.Organization = "  acme  ";
        await AdvanceToStepAsync(wizard, 4);
        wizard.TargetRoot = @"  C:\work-repos  ";

        await wizard.SaveAsync();

        var saved = Assert.Single(_store.GetAll());
        Assert.Equal("Work", saved.Name);
        Assert.Equal("primary org", saved.Description);
        Assert.Equal("acme", saved.Organization);
        Assert.Equal(@"C:\work-repos", saved.TargetRoot);
        Assert.Equal("ghp_token", _vault.TryRetrieve(saved.Id));
    }

    // ---------------------------------------------------------------- save: edits

    [Fact]
    public async Task SaveAsync_Edit_UpdatesFields_PreservesIdAndLastSync_LeavesVaultAlone()
    {
        var account = MakeAccount("Work") with
        {
            LastSyncUtc = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero),
            LastSyncSummary = "Finished: 3 cloned.",
        };
        _store.Save(account, "ghp_original");
        var wizard = NewWizard(account, "ghp_original");
        wizard.Description = "edited";
        wizard.Organization = "acme-2";
        await AdvanceToStepAsync(wizard, 4);
        // Overwrite the vault entry out of band: if SaveAsync wrote the (unchanged)
        // token back, this sentinel would be clobbered with 'ghp_original'.
        _vault.Store(account.Id, "sentinel");

        await wizard.SaveAsync();

        var saved = Assert.Single(_store.GetAll());
        Assert.Equal(account.Id, saved.Id);
        Assert.Equal("edited", saved.Description);
        Assert.Equal("acme-2", saved.Organization);
        Assert.Equal(account.LastSyncUtc, saved.LastSyncUtc);
        Assert.Equal(account.LastSyncSummary, saved.LastSyncSummary);
        Assert.Equal("sentinel", _vault.TryRetrieve(account.Id));
    }

    [Fact]
    public async Task SaveAsync_Edit_WithAChangedToken_UpdatesTheVault()
    {
        var account = MakeAccount("Work");
        _store.Save(account, "ghp_original");
        var wizard = NewWizard(account, "ghp_original");
        wizard.Token = "ghp_rotated";
        await AdvanceToStepAsync(wizard, 4);

        await wizard.SaveAsync();

        Assert.Equal(account.Id, Assert.Single(_store.GetAll()).Id);
        Assert.Equal("ghp_rotated", _vault.TryRetrieve(account.Id));
    }
}
