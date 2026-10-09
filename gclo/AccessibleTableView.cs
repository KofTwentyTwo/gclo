/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUI.TableView;


namespace gclo;


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
      for(int i = 0; i < count; i++)
      {
         DependencyObject child = VisualTreeHelper.GetChild(root, i);
         if(child is T match)
         {
            return match;
         }
         if(child is not ItemsPresenter && FindDescendant<T>(child) is { } nested)
         {
            return nested; // rows live under the ItemsPresenter; the header row does not
         }
      }
      return null;
   }
}
