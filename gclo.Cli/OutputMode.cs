/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>How 'gclo sync' reports progress and the result.</summary>
internal enum OutputMode
{
   /// <summary>Transitions on stdout, failures on stderr, a summary line at the end.</summary>
   Text,

   /// <summary>Failures on stderr and the summary line only.</summary>
   Quiet,

   /// <summary>Nothing until the end, then one JSON document.</summary>
   Json,

   /// <summary>One JSON object per transition on stdout (NDJSON), then the summary object.</summary>
   JsonLines,
}
