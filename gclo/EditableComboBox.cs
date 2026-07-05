using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace gclo
{
    /// <summary>
    /// Workaround for a WinUI platform quirk: an editable ComboBox never renders
    /// Text that was bound before its template loaded — the value sits in the Text
    /// property but the template's inner TextBox stays empty, so a seeded value
    /// (an account workspace's organization) looks blank. Re-assigning Text is an
    /// identical-value no-op, so the inner TextBox must be written directly.
    /// </summary>
    internal static class EditableComboBox
    {
        /// <summary>
        /// Pushes <paramref name="text"/> into the combo's template TextBox (and the
        /// Text property, for good measure). Call from the control's Loaded handler;
        /// enqueued once when the template has not been applied yet at that point.
        /// </summary>
        public static void ReapplyText(ComboBox combo, string text)
        {
            if (FindInnerTextBox(combo) is TextBox inner)
            {
                inner.Text = text;
                inner.SelectionStart = text.Length;
            }
            else
            {
                // Template not applied yet: try once more after this layout pass.
                combo.DispatcherQueue.TryEnqueue(() =>
                {
                    if (FindInnerTextBox(combo) is TextBox late)
                    {
                        late.Text = text;
                        late.SelectionStart = text.Length;
                    }
                });
            }
        }

        private static TextBox? FindInnerTextBox(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox box)
                {
                    return box;
                }
                if (FindInnerTextBox(child) is TextBox nested)
                {
                    return nested;
                }
            }
            return null;
        }
    }
}
