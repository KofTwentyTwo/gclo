/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>An <see cref="IActivityLog"/> that discards everything.</summary>
public sealed class NullActivityLog : IActivityLog
{
   /// <inheritdoc/>
   public string LogDirectory => "";



   /// <inheritdoc/>
   public string CurrentLogFilePath => "";



   /// <inheritdoc/>
   public void Info(string message)
   {
   }



   /// <inheritdoc/>
   public void Error(string message, Exception? exception = null)
   {
   }
}
