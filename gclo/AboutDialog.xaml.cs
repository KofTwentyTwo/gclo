using System;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// About dialog: app identity and version, project link, MIT license notice,
    /// and third-party attributions. The caller must set <see cref="ContentDialog.XamlRoot"/>
    /// before calling ShowAsync.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();
            VersionText.Text = "Version " + GetAppVersion();
        }

        /// <summary>
        /// Returns the build identity synced to the release pipeline: the semantic
        /// version CI injects for the tag (0.1.0-dev on local builds) plus the short
        /// git commit, e.g. "0.1.0-beta.6 (9ac6c2b)". The MSIX package version is
        /// deliberately not used — its numeric quad cannot carry prerelease tags or
        /// commit hashes, so it cannot distinguish a local build from a release.
        /// </summary>
        private static string GetAppVersion()
            => gclo.Engine.BuildVersion.Describe(Assembly.GetExecutingAssembly());
    }
}
