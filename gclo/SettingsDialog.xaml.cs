using System;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Modal editor for <see cref="AppSettings"/> plus the optional default GitHub
    /// token (kept in the <see cref="ITokenVault"/>, never in settings.json). The
    /// dialog only edits and persists values; the caller owns side effects like
    /// applying the theme.
    ///
    /// WinUI 3 requires <c>XamlRoot</c> to be set before <c>ShowAsync</c>. Typical
    /// usage from a Window:
    /// <code>
    /// var dialog = new SettingsDialog(settings, vault, () => hwnd) { XamlRoot = Content.XamlRoot };
    /// if (await dialog.ShowAsync() == ContentDialogResult.Primary)
    /// {
    ///     dialog.ApplyAndSave();
    /// }
    /// </code>
    /// </summary>
    public sealed partial class SettingsDialog : ContentDialog
    {
        private readonly AppSettings _settings;
        private readonly ITokenVault _vault;
        // A ContentDialog has no HWND of its own; the host window supplies one for pickers.
        private readonly Func<nint> _windowHandleProvider;

        /// <summary>Set by the Remove link; the deletion happens on Save.</summary>
        private bool _removeSavedToken;

        /// <summary>Creates the dialog and populates the controls from <paramref name="settings"/>.</summary>
        public SettingsDialog(AppSettings settings, ITokenVault vault, Func<nint> windowHandleProvider)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(vault);
            ArgumentNullException.ThrowIfNull(windowHandleProvider);
            _settings = settings;
            _vault = vault;
            _windowHandleProvider = windowHandleProvider;
            InitializeComponent();

            TargetFolderBox.Text = settings.DefaultTargetFolder;
            ConcurrencyBox.Value = settings.DefaultMaxConcurrency;
            ThemeBox.SelectedIndex = settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            };
            SplashToggle.IsOn = settings.ShowSplashScreen;
            SplashDurationBox.Value = settings.SplashMilliseconds;

            // The saved token is never re-materialized into the box — the box stays
            // empty and means "unchanged"; only typing a new value replaces it.
            RefreshTokenState(hasSaved: _vault.TryRetrieve(AppSettings.DefaultTokenVaultId) is not null);
        }

        /// <summary>
        /// Copies the edited values back into the <see cref="AppSettings"/> instance the
        /// dialog was constructed with, persists them, and applies the default-token
        /// change (store or remove) to the vault. Call after <c>ShowAsync</c> returned
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

            _settings.ShowSplashScreen = SplashToggle.IsOn;
            double splash = SplashDurationBox.Value;
            if (!double.IsNaN(splash))
            {
                _settings.SplashMilliseconds = Math.Clamp(
                    (int)Math.Round(splash),
                    AppSettings.MinSplashMilliseconds,
                    AppSettings.MaxSplashMilliseconds);
            }

            _settings.Save();

            string typed = DefaultTokenBox.Password;
            if (typed.Length > 0)
            {
                _vault.Store(AppSettings.DefaultTokenVaultId, typed);
            }
            else if (_removeSavedToken)
            {
                _vault.Delete(AppSettings.DefaultTokenVaultId);
            }
        }

        private void RemoveTokenButton_Click(object sender, RoutedEventArgs e)
        {
            _removeSavedToken = true;
            DefaultTokenBox.Password = "";
            TokenStateText.Text = "Saved token will be removed on Save.";
            RemoveTokenButton.Visibility = Visibility.Collapsed;
        }

        private void RefreshTokenState(bool hasSaved)
        {
            TokenStateText.Text = hasSaved
                ? "A default token is saved. Leave the box empty to keep it."
                : "No default token saved.";
            RemoveTokenButton.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*"); // required in packaged apps

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                TargetFolderBox.Text = folder.Path;
            }
        }
    }
}
