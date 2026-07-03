using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using gclo.ViewModels;

namespace gclo
{
    /// <summary>Main (and only) window: org sync inputs, progress, and per-repo status list.</summary>
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            ViewModel = new MainViewModel();
            InitializeComponent();

            Title = "gclo - GitHub organization sync";

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
