using System;
using gclo.Services;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Modal editor for <see cref="AppSettings"/>. The dialog only edits and persists the
    /// values; it does not apply the theme or touch any window — the caller owns side effects.
    ///
    /// WinUI 3 requires <c>XamlRoot</c> to be set before <c>ShowAsync</c> (a ContentDialog
    /// created in code has no visual tree to attach to and throws otherwise). Typical usage
    /// from a Window:
    /// <code>
    /// var dialog = new SettingsDialog(settings) { XamlRoot = Content.XamlRoot };
    /// if (await dialog.ShowAsync() == ContentDialogResult.Primary)
    /// {
    ///     dialog.ApplyAndSave(); // writes edits into the AppSettings passed above, then Save()
    ///     // caller applies theme / defaults as it sees fit
    /// }
    /// </code>
    /// </summary>
    public sealed partial class SettingsDialog : ContentDialog
    {
        private readonly AppSettings _settings;

        /// <summary>Creates the dialog and populates the controls from <paramref name="settings"/>.</summary>
        public SettingsDialog(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();

            TargetFolderBox.Text = settings.DefaultTargetFolder;
            ConcurrencyBox.Value = settings.DefaultMaxConcurrency;
            ThemeBox.SelectedIndex = settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            };
        }

        /// <summary>
        /// Copies the edited values back into the <see cref="AppSettings"/> instance the dialog
        /// was constructed with and persists them. Call this after <c>ShowAsync</c> returned
        /// <see cref="ContentDialogResult.Primary"/>.
        /// </summary>
        public void ApplyAndSave()
        {
            _settings.DefaultTargetFolder = TargetFolderBox.Text.Trim();

            // NumberBox.Value is NaN when the field was cleared; keep the previous value then.
            double value = ConcurrencyBox.Value;
            if (!double.IsNaN(value))
            {
                _settings.DefaultMaxConcurrency = Math.Clamp(
                    (int)Math.Round(value), AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
            }

            _settings.Theme = ThemeBox.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System",
            };

            _settings.Save();
        }
    }
}
