using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUI.TableView;

namespace gclo
{
    /// <summary>
    /// <see cref="TableView"/> whose UI Automation tree includes the column header row.
    /// WinUI.TableView 1.5.0 gives headers their own peers (Invoke sorts, the name
    /// composes the sort state) but inherits <c>ListViewAutomationPeer</c>'s children,
    /// which are the rows only: the header row, its headers, and anything hosted in
    /// them (the filter funnels here) are unreachable by tree walking, and the Table
    /// pattern's <c>ColumnHeaders</c> property fails for the same reason. This peer
    /// prepends the header row, so screen readers and the FlaUI suite see the table
    /// the way the hand-rolled header was seen before (#33).
    /// </summary>
    public sealed partial class AccessibleTableView : TableView
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleTableViewPeer(this);

        /// <summary>The template's header row, once the template has been applied; null before.</summary>
        internal TableViewHeaderRow? FindHeaderRow() => FindDescendant<TableViewHeaderRow>(this);

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }
                if (child is not ItemsPresenter && FindDescendant<T>(child) is { } nested)
                {
                    return nested; // rows live under the ItemsPresenter; the header row does not
                }
            }
            return null;
        }
    }

    /// <summary>See <see cref="AccessibleTableView"/>.</summary>
    public sealed partial class AccessibleTableViewPeer : WinUI.TableView.AutomationPeers.TableViewAutomationPeer
    {
        private readonly AccessibleTableView _owner;

        public AccessibleTableViewPeer(AccessibleTableView owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override IList<AutomationPeer> GetChildrenCore()
        {
            IList<AutomationPeer> children = base.GetChildrenCore() ?? new List<AutomationPeer>();
            if (_owner.FindHeaderRow() is not { } headerRow)
            {
                return children;
            }

            // TableViewHeaderRow is a plain Control with no peer of its own, so its
            // column headers (which do have peers) become direct children of the table.
            var withHeaders = new List<AutomationPeer>(children.Count + 8);
            foreach (TableViewColumnHeader header in FindAll<TableViewColumnHeader>(headerRow))
            {
                if (FrameworkElementAutomationPeer.CreatePeerForElement(header) is { } headerPeer)
                {
                    withHeaders.Add(headerPeer);
                }
            }
            withHeaders.AddRange(children);
            return withHeaders;
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    yield return match;
                    continue;
                }
                foreach (T nested in FindAll<T>(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Header content for a table column: the title text plus whatever the column adds
    /// beside it (the filter funnel). TableView names each cell from its column's
    /// header object, so the override makes that "Name, Row 3, alpha" rather than the
    /// Grid's type name.
    /// </summary>
    public sealed partial class ColumnHeaderContent : Grid
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(ColumnHeaderContent), new PropertyMetadata(""));

        /// <summary>The column title, as read by assistive technology.</summary>
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public override string ToString() => Title;
    }
}
