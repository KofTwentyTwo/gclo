/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>A clone into WSL did not complete; the message carries git's or wsl.exe's own explanation.</summary>
public sealed class WslCloneException : Exception
{
   /// <param name="message">What failed, in git's or wsl.exe's words.</param>
   public WslCloneException(string message) : base(message) { }
}
