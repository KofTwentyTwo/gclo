using System;
using System.Collections.Generic;
using System.IO;
using gclo.Engine;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Read-only viewer for the tail of today's activity log, with a shortcut to the
    /// logs folder. The caller must set <see cref="ContentDialog.XamlRoot"/> before
    /// calling ShowAsync.
    /// </summary>
    public sealed partial class LogViewerDialog : ContentDialog
    {
        private const int TailLineCount = 400;

        private readonly IActivityLog _log;

        public LogViewerDialog(IActivityLog log)
        {
            _log = log;
            InitializeComponent();
            LogText.Text = ReadTail(log.CurrentLogFilePath);

            // Recent entries matter most: start scrolled to the end of the tail.
            Opened += (_, _) =>
            {
                LogScroll.UpdateLayout();
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
            };
        }

        /// <summary>
        /// Returns the last <see cref="TailLineCount"/> lines of <paramref name="path"/>,
        /// or a placeholder when the file is missing or unreadable. Never throws: the
        /// viewer is diagnostics UI and must not crash the app over a bad log file.
        /// </summary>
        private static string ReadTail(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return "No log entries yet.";
                }

                // Share ReadWrite: the activity log may append while the dialog reads.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

                return tail.Count == 0
                    ? "No log entries yet."
                    : string.Join(Environment.NewLine, tail);
            }
            catch (Exception ex)
            {
                return "Could not read the log file." + Environment.NewLine + ex.Message;
            }
        }

        private async void OpenLogsFolder_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string directory = _log.LogDirectory;
            if (Directory.Exists(directory))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(directory);
            }
        }
    }
}
