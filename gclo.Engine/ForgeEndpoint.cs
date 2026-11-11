/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;


namespace gclo.Engine;


/// <summary>
/// Where an organization or account lives: the kind of forge and, for a forge that
/// has no one well-known host, the server's address. The one place that says which
/// listers belong to it, so the command line and the desktop app cannot pair an
/// account with the listers of another forge.
/// </summary>
public sealed class ForgeEndpoint : IEquatable<ForgeEndpoint>
{
   /// <summary>github.com.</summary>
   public static ForgeEndpoint GitHub { get; } = new(ForgeKind.GitHub, null);



   /// <summary>The kind of forge.</summary>
   public ForgeKind Kind { get; }

   /// <summary>The server's base address, as <see cref="GiteaServerUrl"/> normalizes it; null for GitHub.</summary>
   public Uri? Server { get; }

   /// <summary>How messages and lists name the forge: "GitHub", or the server's host (with its port when that is not 443).</summary>
   public string DisplayName => Server?.Authority ?? "GitHub";



   private ForgeEndpoint(ForgeKind kind, Uri? server)
   {
      Kind = kind;
      Server = server;
   }



   /// <summary>
   /// The endpoint for a kind of forge and what was given as its server address, or
   /// the reason there is none: GitHub takes no address, a Gitea server needs one
   /// that <see cref="GiteaServerUrl.TryNormalize"/> accepts.
   /// </summary>
   /// <param name="kind">The kind of forge.</param>
   /// <param name="server">The server's address as it was typed or stored; null or blank for none.</param>
   /// <param name="endpoint">The endpoint; null when there is none.</param>
   /// <param name="reason">Why there is none; null when there is one.</param>
   public static bool TryCreate(
       ForgeKind kind, string? server, [NotNullWhen(true)] out ForgeEndpoint? endpoint, [NotNullWhen(false)] out string? reason)
   {
      endpoint = null;
      bool given = !string.IsNullOrWhiteSpace(server);
      switch(kind)
      {
         case ForgeKind.GitHub:
            if(given)
            {
               reason = "GitHub has no server address to choose: it is always github.com";
               return false;
            }
            endpoint = GitHub;
            reason = null;
            return true;

         case ForgeKind.Gitea:
            if(!given)
            {
               reason = "a Gitea account needs the address of its server";
               return false;
            }
            if(!GiteaServerUrl.TryNormalize(server, out Uri? address, out string? whyNot))
            {
               ///////////////////////////////////////////////////////////////////////
               // The address is not repeated: it may have been refused for the    //
               // password or the token that is in it.                             //
               ///////////////////////////////////////////////////////////////////////
               reason = $"the server address cannot be used: {whyNot}";
               return false;
            }
            endpoint = new ForgeEndpoint(ForgeKind.Gitea, address);
            reason = null;
            return true;

         default:
            reason = $"'{kind}' is not a kind of forge gclo knows";
            return false;
      }
   }



   /// <summary>The lister of this forge's repositories. For a Gitea server it sends tokens to <see cref="Server"/> only.</summary>
   public IRepositoryLister CreateRepositoryLister()
       => Server is null ? new GitHubRepositoryLister() : new GiteaRepositoryLister(Server);



   /// <summary>The lister of this forge's organizations. For a Gitea server it sends tokens to <see cref="Server"/> only.</summary>
   public IOrganizationLister CreateOrganizationLister()
       => Server is null ? new GitHubOrganizationLister() : new GiteaOrganizationLister(Server);



   /// <inheritdoc/>
   public bool Equals(ForgeEndpoint? other)
       => other is not null && Kind == other.Kind && Equals(Server, other.Server);



   /// <inheritdoc/>
   public override bool Equals(object? obj) => Equals(obj as ForgeEndpoint);



   /// <inheritdoc/>
   public override int GetHashCode() => HashCode.Combine(Kind, Server);



   /// <summary>The <see cref="DisplayName"/>.</summary>
   public override string ToString() => DisplayName;
}
