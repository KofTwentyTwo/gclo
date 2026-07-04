using System; // the GetAwaiter extension for IAsyncOperation lives here (CsWinRT)
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Serializes ContentDialog display. WinUI allows a single open ContentDialog
    /// per XamlRoot, and a second ShowAsync does not queue — it throws a
    /// COMException which, escaping an async void event handler, kills the process
    /// with a stowed exception (0xc000027b). Every dialog in the app opens through
    /// <see cref="ShowAsync"/>, which ignores the request while another dialog is
    /// on screen — the same no-op a user expects from commands behind a modal.
    /// All calls run on the single UI thread, so the flag needs no locking.
    /// </summary>
    internal static class DialogGuard
    {
        private static bool _dialogOpen;

        /// <summary>
        /// Shows <paramref name="dialog"/> unless another dialog is on screen.
        /// Returns the dialog result, or null when the request was ignored —
        /// callers treat null like a dismissal.
        /// </summary>
        public static async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog)
        {
            if (_dialogOpen)
            {
                return null;
            }

            _dialogOpen = true;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _dialogOpen = false;
            }
        }
    }
}
