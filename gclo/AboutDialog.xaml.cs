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
        /// Returns the app version formatted as "major.minor.build". Prefers the MSIX
        /// package version; Package.Current throws when the app runs unpackaged
        /// (no package identity), in which case the assembly version is used instead.
        /// </summary>
        private static string GetAppVersion()
        {
            try
            {
                var packageVersion = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}";
            }
            catch (Exception)
            {
                Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (assemblyVersion is null)
                {
                    return "1.0.0";
                }

                // Version.Build is -1 when the assembly version has fewer than three parts.
                int build = assemblyVersion.Build < 0 ? 0 : assemblyVersion.Build;
                return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{build}";
            }
        }
    }
}
