using gclo.Engine;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace gclo;

/// <summary>Static helpers for x:Bind function bindings in WorkspacePage.xaml.</summary>
public static class StatusFormat
{
    /// <summary>
    /// Theme- and high-contrast-aware brush for a status. Resolved from the app's
    /// resources on every call so the current theme's instance is returned; x:Bind
    /// re-invokes this whenever the bound Status changes.
    /// </summary>
    public static Brush BrushFor(SyncStatus status) => ResourceBrush(status switch
    {
        SyncStatus.Cloning or SyncStatus.Pulling => "SystemFillColorAttentionBrush",
        SyncStatus.Done => "SystemFillColorSuccessBrush",
        SyncStatus.Failed => "SystemFillColorCriticalBrush",
        SyncStatus.Canceled => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    /// <summary>
    /// Segoe Fluent Icons glyph for a status, shown beside the status text so state
    /// is never communicated by color alone.
    /// </summary>
    public static string GlyphFor(SyncStatus status) => status switch
    {
        SyncStatus.Queued => "",   // Recent (clock)
        SyncStatus.Cloning => "",  // Download
        SyncStatus.Pulling => "",  // Sync
        SyncStatus.Done => "",     // CheckMark
        SyncStatus.Failed => "",   // Error
        SyncStatus.Canceled => "", // Cancel
        _ => "",
    };

    /// <summary>Screen-reader name for a row's path-recovery link.</summary>
    public static string ResolveAutomationName(string repoName)
        => $"Resolve invalid paths for {repoName}";

    private static Brush ResourceBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            // Unreachable with XamlControlsResources merged; a visible-but-neutral
            // fallback beats crashing the row template if a key ever disappears.
            : new SolidColorBrush(Colors.Gray);

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
