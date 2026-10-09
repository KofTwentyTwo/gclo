/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using WinUI.TableView;


namespace gclo;


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
      if(_owner.FindHeaderRow() is not { } headerRow)
      {
         return children;
      }

      // TableViewHeaderRow is a plain Control with no peer of its own, so its
      // column headers (which do have peers) become direct children of the table.
      var withHeaders = new List<AutomationPeer>(children.Count + 8);
      foreach(TableViewColumnHeader header in FindAll<TableViewColumnHeader>(headerRow))
      {
         if(FrameworkElementAutomationPeer.CreatePeerForElement(header) is { } headerPeer)
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
      for(int i = 0; i < count; i++)
      {
         DependencyObject child = VisualTreeHelper.GetChild(root, i);
         if(child is T match)
         {
            yield return match;
            continue;
         }
         foreach(T nested in FindAll<T>(child))
         {
            yield return nested;
         }
      }
   }
}
