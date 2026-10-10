/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// Where an account's GitHub token comes from. <see cref="Own"/> is the account's
/// own vault entry (keyed by its id); <see cref="Default"/> is the default token
/// saved in Settings (<see cref="AppSettings.DefaultTokenVaultId"/>), read at use
/// time, so replacing the default token in Settings rotates every such account
/// at once (docs/plans/default-token-ux.md).
/// </summary>
public enum TokenSource
{
   /// <summary>The account's own token, stored under its id. The on-disk default.</summary>
   Own = 0,

   /// <summary>The default token from Settings, resolved whenever the account is used.</summary>
   Default = 1,
}
