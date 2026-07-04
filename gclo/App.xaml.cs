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
        /// Constructed by Program.Main inside Application.Start after VelopackApp handled
        /// install/update hooks — see Program.cs.
        /// </summary>
        public App()
        {
            InitializeComponent();

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
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
