using gclo.Engine;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace gclo;

/// <summary>Static helpers for x:Bind function bindings in MainWindow.xaml.</summary>
public static class StatusFormat
{
    // Created lazily on first x:Bind evaluation, which happens on the UI thread.
    private static readonly SolidColorBrush QueuedBrush = new(Colors.Gray);
    private static readonly SolidColorBrush ActiveBrush = new(Colors.DodgerBlue);
    private static readonly SolidColorBrush DoneBrush = new(Colors.ForestGreen);
    private static readonly SolidColorBrush FailedBrush = new(Colors.Crimson);
    private static readonly SolidColorBrush CanceledBrush = new(Colors.Orange);

    public static Brush BrushFor(SyncStatus status) => status switch
    {
        SyncStatus.Cloning or SyncStatus.Pulling => ActiveBrush,
        SyncStatus.Done => DoneBrush,
        SyncStatus.Failed => FailedBrush,
        SyncStatus.Canceled => CanceledBrush,
        _ => QueuedBrush,
    };

    public static Visibility VisibleIf(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Maps the persisted theme string to a XAML theme ("System"/unknown => Default).</summary>
    public static ElementTheme ToElementTheme(string theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static string Fraction(int completed, int total)
        => $"{completed} / {total}";
}
