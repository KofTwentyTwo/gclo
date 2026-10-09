/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// The message behind <see cref="WorkspaceViewModel.AnnouncementRequested"/>: text
/// that assistive technology should read out (a per-repository failure, for example).
/// </summary>
public sealed class AnnouncementEventArgs : EventArgs
{
   /// <param name="message">The text to announce.</param>
   public AnnouncementEventArgs(string message)
   {
      ArgumentNullException.ThrowIfNull(message);
      Message = message;
   }



   /// <summary>The text to announce.</summary>
   public string Message { get; }
}
