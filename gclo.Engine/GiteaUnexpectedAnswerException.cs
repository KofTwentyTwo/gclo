/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// What came back from the address of a Gitea server is not what the Gitea API
/// answers: not JSON, JSON of another shape, an answer without end, a list without
/// end. Internal to the Gitea listers; <see cref="GiteaApi.Translate"/> puts it into words.
/// </summary>
internal sealed class GiteaUnexpectedAnswerException : Exception
{
   /// <summary>Creates the exception with what was unexpected and, where there is one, the cause.</summary>
   public GiteaUnexpectedAnswerException(string what, Exception? innerException = null)
       : base(what, innerException)
   {
   }
}
