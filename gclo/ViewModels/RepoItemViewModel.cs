using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>One row in the repository status list.</summary>
public sealed partial class RepoItemViewModel : ObservableObject
{
    public RepoItemViewModel(string name)
    {
        Name = name;
        Status = SyncStatus.Queued;
    }

    /// <summary>Repository name; never changes after construction.</summary>
    public string Name { get; }

    [ObservableProperty]
    public partial SyncStatus Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial double? Percent { get; set; }

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

    partial void OnStatusChanged(SyncStatus value) => OnPropertyChanged(nameof(StatusText));

    partial void OnPercentChanged(double? value) => OnPropertyChanged(nameof(StatusText));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
