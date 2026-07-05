using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using gclo.Engine;
using Microsoft.UI.Xaml;

namespace gclo
{
    /// <summary>
    /// Non-modal activity-log window: stays open beside the main window while syncs
    /// run, tails the current log file live (timer + follow mode), and shows the raw
    /// technical text — timestamps, levels, full exception stacks — in monospace.
    /// <see cref="MainWindow"/> keeps at most one instance and re-activates it.
    /// </summary>
    public sealed partial class LogWindow : Window
    {
        private const int TailLineCount = 2000;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private readonly IActivityLog _log;
        private readonly DispatcherTimer _refresh = new() { Interval = RefreshInterval };

        private long _lastLength = -1;
        private bool _errorsOnly;

        public LogWindow(IActivityLog log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            InitializeComponent();

            LogPathText.Text = _log.CurrentLogFilePath;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(680 * scale)));

            _refresh.Tick += (_, _) => RefreshIfChanged();
            _refresh.Start();
            Closed += (_, _) => _refresh.Stop();

            Reload(scrollToEnd: true);
        }

        private void ErrorsOnlyToggle_Click(object sender, RoutedEventArgs e)
        {
            _errorsOnly = ErrorsOnlyToggle.IsChecked == true;
            Reload(scrollToEnd: true);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(LogText.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }

        private async void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            string directory = _log.LogDirectory;
            if (Directory.Exists(directory))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(directory);
            }
        }

        /// <summary>Timer tick: reload only when the file grew (or rolled) and Follow is on.</summary>
        private void RefreshIfChanged()
        {
            if (FollowToggle.IsChecked != true)
            {
                return;
            }

            try
            {
                string path = _log.CurrentLogFilePath;
                long length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length != _lastLength)
                {
                    LogPathText.Text = path; // the day can roll over while the window is open
                    Reload(scrollToEnd: true);
                }
            }
            catch
            {
                // Diagnostics UI must never take the app down over an unreadable file.
            }
        }

        private void Reload(bool scrollToEnd)
        {
            IReadOnlyList<string> lines = ReadTail(_log.CurrentLogFilePath, out _lastLength);
            if (_errorsOnly)
            {
                lines = FilterToErrorEntries(lines);
            }

            LogText.Text = lines.Count == 0
                ? (_errorsOnly ? "No errors logged. Good." : "No log entries yet.")
                : string.Join(Environment.NewLine, lines);

            if (scrollToEnd)
            {
                LogScroll.UpdateLayout();
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
            }
        }

        /// <summary>
        /// Last <see cref="TailLineCount"/> lines of the file (shared for concurrent
        /// appends); never throws — this is diagnostics UI.
        /// </summary>
        private static IReadOnlyList<string> ReadTail(string path, out long length)
        {
            length = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return [];
                }

                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                length = stream.Length;
                using var reader = new StreamReader(stream);

                var tail = new Queue<string>(TailLineCount);
                while (reader.ReadLine() is string line)
                {
                    if (tail.Count == TailLineCount)
                    {
                        tail.Dequeue();
                    }
                    tail.Enqueue(line);
                }
                return [.. tail];
            }
            catch (Exception ex)
            {
                return ["Could not read the log file.", ex.Message];
            }
        }

        /// <summary>
        /// Keeps [ERROR] entries including their continuation lines (exception text
        /// spans lines until the next timestamped entry starts).
        /// </summary>
        private static List<string> FilterToErrorEntries(IReadOnlyList<string> lines)
        {
            var kept = new List<string>();
            bool inError = false;
            foreach (string line in lines)
            {
                if (EntryStart().IsMatch(line))
                {
                    inError = line.Contains("[ERROR]", StringComparison.Ordinal);
                }
                if (inError)
                {
                    kept.Add(line);
                }
            }
            return kept;
        }

        [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}")]
        private static partial Regex EntryStart();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
