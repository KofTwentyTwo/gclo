/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;


namespace gclo;


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
      // When the value is one of the items, selecting it is the clean path: the
      // combo renders it like a user's pick, with no placeholder or dropdown side
      // effects (writing text into a focused editable combo opens its list, and
      // closing that list throws the text away). Free text still goes to the box.
      if(SelectItem(combo, text))
      {
         return;
      }
      if(FindInnerTextBox(combo) is TextBox inner)
      {
         Apply(inner, text);
      }
      else
      {
         // Template not applied yet: try once more after this layout pass.
         combo.DispatcherQueue.TryEnqueue(() =>
         {
            if(!SelectItem(combo, text) && FindInnerTextBox(combo) is TextBox late)
            {
               Apply(late, text);
            }
         });
      }
   }



   private static bool SelectItem(ComboBox combo, string text)
   {
      foreach(object item in combo.Items)
      {
         if(item is string candidate && string.Equals(candidate, text, System.StringComparison.Ordinal))
         {
            combo.SelectedItem = item;
            return true;
         }
      }
      return false;
   }



   /// <summary>
   /// Writes the text and re-evaluates the placeholder: a TextBox whose Text is set
   /// from code while it was collapsed keeps painting its placeholder over the text
   /// (the wizard's step 3 box showed "Choose from the list" over a filled value);
   /// touching PlaceholderText makes the control recompute that visual state.
   /// </summary>
   private static void Apply(TextBox box, string text)
   {
      box.Text = text;
      box.SelectionStart = text.Length;
      string placeholder = box.PlaceholderText;
      box.PlaceholderText = "";
      box.PlaceholderText = placeholder;
   }



   private static TextBox? FindInnerTextBox(DependencyObject root)
   {
      int count = VisualTreeHelper.GetChildrenCount(root);
      for(int i = 0; i < count; i++)
      {
         DependencyObject child = VisualTreeHelper.GetChild(root, i);
         if(child is TextBox box)
         {
            return box;
         }
         if(FindInnerTextBox(child) is TextBox nested)
         {
            return nested;
         }
      }
      return null;
   }
}
