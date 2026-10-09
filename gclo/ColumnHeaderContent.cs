/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;


namespace gclo;


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
