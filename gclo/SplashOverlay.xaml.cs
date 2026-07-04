using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Branded startup splash rendered as a full-window overlay inside
    /// <see cref="MainWindow"/>. An overlay instead of a separate splash window by
    /// design: closing a real window while assistive technology or UI automation is
    /// querying it crashes the process with a stowed XAML exception, whereas fading
    /// out and removing an element is ordinary, race-free XAML work.
    /// </summary>
    public sealed partial class SplashOverlay : UserControl
    {
        private bool _dismissed;

        /// <summary>Builds the overlay and stamps the build identity line.</summary>
        public SplashOverlay()
        {
            InitializeComponent();
            VersionText.Text = gclo.Engine.BuildVersion.Describe(Assembly.GetExecutingAssembly());
        }

        /// <summary>
        /// Fades the overlay out and removes it from its parent panel. Safe to call
        /// more than once; only the first call does anything.
        /// </summary>
        public async Task DismissAsync()
        {
            if (_dismissed)
            {
                return;
            }
            _dismissed = true;

            // Input must fall through to the app the moment dismissal starts.
            IsHitTestVisible = false;

            var completion = new TaskCompletionSource();
            FadeOut.Completed += (_, _) => completion.TrySetResult();
            FadeOut.Begin();
            await completion.Task;

            (Parent as Panel)?.Children.Remove(this);
        }
    }
}
