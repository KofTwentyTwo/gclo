using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Drives the four-step add/edit account wizard: identity (name + description), token
/// (validated by listing the organizations it can see), organization, and destination.
/// <see cref="TryAdvanceAsync"/> gates each step; <see cref="SaveAsync"/> persists the
/// result through the <see cref="AccountsStore"/>, writing the token to the vault only
/// when the user actually changed it (or always, for a new account).
/// </summary>
public sealed partial class AccountWizardViewModel : ObservableObject
{
    private readonly AccountsStore _store;
    private readonly IOrganizationLister _orgLister;
    private readonly Account? _existing;

    /// <summary>What the token box was seeded with; unchanged means "leave the vault alone".</summary>
    private readonly string _seededToken;

    /// <summary>
    /// A wizard for a new account seeded from <paramref name="defaults"/>, or — when
    /// <paramref name="existing"/> is given — an edit wizard seeded from that account
    /// and <paramref name="existingToken"/> (the vault's current token, or null when
    /// the vault has no entry for it).
    /// </summary>
    public AccountWizardViewModel(
        AccountsStore store,
        IOrganizationLister orgLister,
        AppSettings defaults,
        Account? existing = null,
        string? existingToken = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(orgLister);
        ArgumentNullException.ThrowIfNull(defaults);
        _store = store;
        _orgLister = orgLister;
        _existing = existing;
        _seededToken = existingToken ?? "";

        Step = 1;
        NameError = "";
        TokenError = "";
        Token = _seededToken;

        if (existing is null)
        {
            Name = "";
            Description = "";
            Organization = "";
            TargetRoot = defaults.DefaultTargetFolder;
            CreateOrgSubfolder = false;
            MaxConcurrency = defaults.DefaultMaxConcurrency;
        }
        else
        {
            Name = existing.Name;
            Description = existing.Description;
            Organization = existing.Organization;
            TargetRoot = existing.TargetRoot;
            CreateOrgSubfolder = existing.CreateOrgSubfolder;
            MaxConcurrency = existing.MaxConcurrency;
        }
    }

    /// <summary>Current wizard step, 1 (identity) through 4 (destination).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    public partial int Step { get; set; }

    /// <summary>Display name for the account; required and unique across accounts.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Optional free-form note about what the account is for.</summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>GitHub personal access token; validated when leaving step 2.</summary>
    [ObservableProperty]
    public partial string Token { get; set; }

    /// <summary>Organization (or user) to sync; picked from <see cref="Organizations"/> or typed.</summary>
    [ObservableProperty]
    public partial string Organization { get; set; }

    /// <summary>Folder the sync targets; see <see cref="CreateOrgSubfolder"/>.</summary>
    [ObservableProperty]
    public partial string TargetRoot { get; set; }

    /// <summary>When set, clones land under TargetRoot\Organization rather than TargetRoot.</summary>
    [ObservableProperty]
    public partial bool CreateOrgSubfolder { get; set; }

    /// <summary>Parallel clone/pull count; kept within the <see cref="AppSettings"/> range.</summary>
    [ObservableProperty]
    public partial int MaxConcurrency { get; set; }

    /// <summary>True while step 2 is validating the token against the organization lister.</summary>
    [ObservableProperty]
    public partial bool IsValidatingToken { get; set; }

    /// <summary>Step 1's validation message; empty when the name is acceptable.</summary>
    [ObservableProperty]
    public partial string NameError { get; set; }

    /// <summary>Step 2's validation message; empty when the token was accepted.</summary>
    [ObservableProperty]
    public partial string TokenError { get; set; }

    /// <summary>Organizations the validated token can see; feeds step 3's editable dropdown.</summary>
    public ObservableCollection<string> Organizations { get; } = new();

    /// <summary>True on step 1, where there is no step to go back to.</summary>
    public bool IsFirstStep => Step == 1;

    /// <summary>True on step 4, where advancing means saving instead of moving on.</summary>
    public bool IsLastStep => Step == 4;

    /// <summary>True when the wizard edits an existing account rather than creating one.</summary>
    public bool IsEditing => _existing is not null;

    /// <summary>Dialog title matching the wizard's mode.</summary>
    public string Title => IsEditing ? "Edit account" : "Add account";

    partial void OnMaxConcurrencyChanged(int value)
    {
        int clamped = Math.Clamp(value, AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
        if (clamped != value)
        {
            MaxConcurrency = clamped;
        }
    }

    /// <summary>
    /// Validates the current step. Steps 1-3 advance and return true on success; step 4
    /// returns true without advancing (the host then calls <see cref="SaveAsync"/>). On
    /// failure the wizard stays put, with the step's error message set where one exists
    /// (<see cref="NameError"/> on step 1, <see cref="TokenError"/> on step 2).
    /// </summary>
    public async Task<bool> TryAdvanceAsync()
    {
        switch (Step)
        {
            case 1:
                {
                    string name = Name.Trim();
                    if (name.Length == 0)
                    {
                        NameError = "Enter a name for this account.";
                        return false;
                    }
                    Account? clash = _store.FindByName(name);
                    if (clash is not null && clash.Id != _existing?.Id)
                    {
                        NameError = $"An account named '{clash.Name}' already exists.";
                        return false;
                    }
                    NameError = "";
                    Step = 2;
                    return true;
                }
            case 2:
                {
                    // The lister is the validation: it fails on a rejected or rate-limited
                    // token and returns the organizations the dropdown offers otherwise.
                    IsValidatingToken = true;
                    try
                    {
                        var organizations = await _orgLister.ListOrganizationsAsync(Token.Trim());
                        Organizations.Clear();
                        foreach (string organization in organizations)
                        {
                            Organizations.Add(organization);
                        }
                        TokenError = "";
                    }
                    catch (Exception ex)
                    {
                        TokenError = ex.Message;
                        return false;
                    }
                    finally
                    {
                        IsValidatingToken = false;
                    }
                    Step = 3;
                    return true;
                }
            case 3:
                if (string.IsNullOrWhiteSpace(Organization))
                {
                    return false; // the dropdown allows free text, but not nothing
                }
                Step = 4;
                return true;
            default:
                // Step 4: valid means "ready to save"; the host closes via SaveAsync.
                return !string.IsNullOrWhiteSpace(TargetRoot);
        }
    }

    /// <summary>Returns to the previous step; a no-op on the first step.</summary>
    public void GoBack()
    {
        if (!IsFirstStep)
        {
            Step--;
        }
    }

    /// <summary>
    /// Persists the wizard's account: a new account gets a fresh id and always writes
    /// its token to the vault; an edit keeps the existing id and last-sync fields and
    /// touches the vault only when the token was changed. All string inputs are trimmed.
    /// </summary>
    public Task SaveAsync()
    {
        _store.Save(BuildAccount(), TokenChanged ? Token.Trim() : null);
        return Task.CompletedTask;
    }

    /// <summary>New accounts always persist their token; edits only when it was altered.</summary>
    private bool TokenChanged
        => !IsEditing || !string.Equals(Token, _seededToken, StringComparison.Ordinal);

    private Account BuildAccount() => new()
    {
        Id = _existing?.Id ?? Guid.NewGuid(),
        Name = Name.Trim(),
        Description = Description.Trim(),
        Organization = Organization.Trim(),
        TargetRoot = TargetRoot.Trim(),
        CreateOrgSubfolder = CreateOrgSubfolder,
        MaxConcurrency = Math.Clamp(MaxConcurrency, AppSettings.MinConcurrency, AppSettings.MaxConcurrency),
        LastSyncUtc = _existing?.LastSyncUtc,
        LastSyncSummary = _existing?.LastSyncSummary,
    };
}
