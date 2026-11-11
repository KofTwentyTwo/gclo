/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.Cli;


/// <summary>
/// Collects and resolves the options that say where an organization lives:
/// <c>--forge github|gitea</c> and, for a Gitea server, <c>--server &lt;address&gt;</c>.
/// Shared by the commands that talk to a forge. Without either, the forge is GitHub;
/// a server alone means a Gitea server, the only kind that has one to name.
/// </summary>
internal sealed class ForgeOptions
{
   private string? _forge;

   private string? _server;



   /// <summary>True when <c>--forge</c> or <c>--server</c> was given.</summary>
   public bool WasGiven => _forge is not null || _server is not null;



   /// <summary>Consumes the reader's current option when it is one of these.</summary>
   public bool TryConsume(OptionReader reader)
   {
      switch(reader.Current)
      {
         case "--forge":
            _forge = reader.RequireValue();
            return true;
         case "--server":
            _server = reader.RequireValue();
            return true;
         default:
            return false;
      }
   }



   /// <summary>The kind of forge the options name: what <c>--forge</c> says, else Gitea when a server was given, else GitHub.</summary>
   /// <exception cref="CliUsageException"><c>--forge</c> names no kind gclo knows.</exception>
   public ForgeKind ResolveKind()
   {
      if(_forge is null)
      {
         return _server is null ? ForgeKind.GitHub : ForgeKind.Gitea;
      }
      return _forge.Trim().ToUpperInvariant() switch
      {
         "GITHUB" => ForgeKind.GitHub,
         "GITEA" => ForgeKind.Gitea,
         // What was given is not repeated: an address typed after the wrong option may hold a password or a token.
         _ => throw new CliUsageException("--forge takes github or gitea. (The address of a server goes after --server.)"),
      };
   }



   /// <summary>The place the options name.</summary>
   /// <exception cref="CliUsageException">The options name no usable place (GitHub with a server, Gitea without one, an address that is not HTTPS).</exception>
   public ForgeEndpoint Resolve()
       => ForgeEndpoint.TryCreate(ResolveKind(), _server, out ForgeEndpoint? endpoint, out string? reason)
           ? endpoint
           : throw new CliUsageException(char.ToUpperInvariant(reason[0]) + reason[1..] + ".");
}
