using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>One row in the repository table: the descriptor plus selection and live sync state.</summary>
public sealed partial class RepoItemViewModel : ObservableObject
{
    public RepoItemViewModel(RepoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        Status = SyncStatus.Queued;
        IsSelected = true;
    }

    /// <summary>The repository this row represents; never changes after construction.</summary>
    public RepoDescriptor Descriptor { get; }

    /// <summary>Repository name; used as the local folder name.</summary>
    public string Name => Descriptor.Name;

    /// <summary>Whether this repository participates in the next sync.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial SyncStatus Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial double? Percent { get; set; }

    /// <summary>
    /// The Windows-invalid paths that made the last sync fail, when that was the cause
    /// (set from the engine's failure progress payload); null for any other failure.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<InvalidPathInfo>? InvalidPaths { get; set; }

    /// <summary>True when the row failed because of Windows-invalid paths and can be resolved.</summary>
    public bool HasPathIssue => InvalidPaths is { Count: > 0 };

    /// <summary>Default branch name, or empty for an empty repository.</summary>
    public string BranchText => Descriptor.DefaultBranch ?? "";

    /// <summary>Whether the repository is archived (still cloneable, read-only).</summary>
    public bool IsArchived => Descriptor.IsArchived;

    /// <summary>True while a git operation is in flight and a progress bar should show.</summary>
    public bool ShowProgress => Status is SyncStatus.Cloning or SyncStatus.Pulling;

    /// <summary>True when progress has no percentage (pulls report none).</summary>
    public bool IsIndeterminate => Status == SyncStatus.Pulling;

    /// <summary>Clone progress in [0, 1] for determinate progress bars.</summary>
    public double ProgressValue => Percent ?? 0;

    /// <summary>Human-readable status, including clone percentage when known.</summary>
    public string StatusText => Status switch
    {
        SyncStatus.Queued => "Queued",
        SyncStatus.Cloning when Percent is double p => $"Cloning {p * 100:0}%",
        SyncStatus.Cloning => "Cloning",
        SyncStatus.Pulling => "Pulling",
        SyncStatus.Done => "Done",
        SyncStatus.Failed => "Failed",
        SyncStatus.Canceled => "Canceled",
        _ => Status.ToString(),
    };

    /// <summary>True when there is an error message to show inline.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnStatusChanged(SyncStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    partial void OnPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressValue));
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnInvalidPathsChanged(IReadOnlyList<InvalidPathInfo>? value)
        => OnPropertyChanged(nameof(HasPathIssue));
}
