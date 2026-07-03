using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using gclo.Services;
using gclo.ViewModels;

namespace gclo
{
    /// <summary>Main (and only) window: org sync inputs, progress, and per-repo status list.</summary>
    public sealed partial class MainWindow : Window
    {
        private const string RepoUrl = "https://github.com/KofTwentyTwo/gclo";

        private readonly AppSettings _settings;
        private readonly UpdateService _updateService = new();

        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            ViewModel = new MainViewModel();
            InitializeComponent();

            Title = "gclo - GitHub organization sync";

            _settings = AppSettings.Load();
            ApplySettings();

            // AppWindow.Resize takes physical pixels; scale by the monitor DPI so the
            // window is the same visual size at 150%/200% display scaling.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(750 * scale)));

            // Best effort: stop in-flight git work when the window closes so clones are
            // not killed mid-checkout (a killed clone is repaired on the next run).
            Closed += (_, _) =>
            {
                if (ViewModel.SyncCancelCommand.CanExecute(null))
                {
                    ViewModel.SyncCancelCommand.Execute(null);
                }
            };
        }

        private void ApplySettings()
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = AppSettings.ToElementTheme(_settings.Theme);
            }
            if (string.IsNullOrWhiteSpace(ViewModel.TargetFolder)
                && !string.IsNullOrWhiteSpace(_settings.DefaultTargetFolder))
            {
                ViewModel.TargetFolder = _settings.DefaultTargetFolder;
            }
            ViewModel.MaxConcurrency = _settings.DefaultMaxConcurrency;
        }

        private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                dialog.ApplyAndSave();
                ApplySettings();
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private async void GitHubMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl));
        }

        private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }

        private async void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_updateService.IsSupported)
            {
                await ShowUpdateMessageAsync(
                    "Check for updates",
                    "Updates are only available in installed builds.");
                return;
            }

            var result = await _updateService.CheckAsync();
            if (result.Error is not null)
            {
                await ShowUpdateMessageAsync(
                    "Check for updates",
                    $"Could not check for updates.\n{result.Error}");
                return;
            }

            if (result.AvailableVersion is null)
            {
                string current = _updateService.CurrentVersion is string v ? $" (v{v})" : "";
                await ShowUpdateMessageAsync("Check for updates", $"You are up to date{current}.");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Update available",
                Content = $"gclo v{result.AvailableVersion} is available. "
                    + "The app will restart to finish installing the update.",
                PrimaryButtonText = "Update and restart",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // On success this exits the process to restart into the new version,
            // so reaching the line below means the update did not go through.
            string? error = await _updateService.DownloadAndApplyAsync();
            if (error is not null)
            {
                await ShowUpdateMessageAsync("Update failed", error);
            }
        }

        private async Task ShowUpdateMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // PasswordBox does not support reliable two-way x:Bind on Password;
        // mirror it into the view model by hand.
        private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Token = ((PasswordBox)sender).Password;
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*"); // required in packaged apps

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.TargetFolder = folder.Path;
            }
        }
    }
}
