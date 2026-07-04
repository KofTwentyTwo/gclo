using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml;

namespace gclo
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Where the unhandled-exception net writes; set by MainWindow once the shared
        /// activity log exists. Null only during the first instants of startup.
        /// </summary>
        internal static IActivityLog? CrashLog { get; set; }

        /// <summary>
        /// Constructed by Program.Main inside Application.Start after VelopackApp handled
        /// install/update hooks — see Program.cs.
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Last-resort net: an exception escaping an async void event handler is
            // rethrown on the dispatcher with no frame above it, and the process dies
            // as a stowed exception (0xc000027b) with nothing in the log. The known
            // collision (two ContentDialogs) is prevented at the source by
            // DialogGuard; this catches whatever else slips through, records it, and
            // keeps the app running.
            UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    CrashLog?.Error($"Unhandled UI exception: {e.Message}", e.Exception);
                }
                catch
                {
                    // Logging must never turn a survivable error fatal; the stowed
                    // Exception property itself can throw when type info was lost.
                }
            };

            // App-level so Application.Current.Resources theme lookups (the status
            // brushes) agree with the visible theme. Only settable here, before any
            // window exists; mid-session theme changes still use the root element's
            // RequestedTheme, and status brushes adopt them as row statuses update.
            string theme = AppSettings.Load().Theme;
            if (theme == "Light")
            {
                RequestedTheme = ApplicationTheme.Light;
            }
            else if (theme == "Dark")
            {
                RequestedTheme = ApplicationTheme.Dark;
            }
        }

        /// <summary>
        /// Invoked when the application is launched. The branded splash is an overlay
        /// inside <see cref="MainWindow"/> (see <see cref="SplashOverlay"/> for why a
        /// separate splash window is a UIA crash hazard).
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
