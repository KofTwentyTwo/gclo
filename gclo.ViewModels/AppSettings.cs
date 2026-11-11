/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// User preferences persisted as JSON at settings.json under
/// <see cref="GcloPaths.DataRoot"/> (default %LOCALAPPDATA%\KofTwentyTwo\gclo, overridable via
/// GCLO_DATA_DIR). Uses Environment.GetFolderPath (not Windows.Storage.ApplicationData)
/// so the same path works whether the app runs packaged or unpackaged.
/// </summary>
public sealed class AppSettings
{
   public const int MinConcurrency = 1;

   public const int MaxConcurrency = 64;

   public const int DefaultConcurrency = 8;

   public const int MinSplashMilliseconds = 500;

   public const int MaxSplashMilliseconds = 30_000;

   public const int DefaultSplashMilliseconds = 5_000;

   /// <summary>
   /// Vault id under which the optional default GitHub token is stored. The token
   /// itself lives only in the <see cref="ITokenVault"/> (Credential Manager) —
   /// never in this settings file.
   /// </summary>
   public static readonly Guid DefaultTokenVaultId = new("d0f0e0c0-9c40-4a5e-8f3a-5b1e6f7a2c11");

   /// <summary>
   /// The address of the Gitea server a default token is saved for (Settings); null
   /// when there is none. The token itself is in the vault, under an entry named
   /// after this address (<see cref="DefaultTokens.EntryFor"/>), never in this file.
   /// </summary>
   public string? DefaultGiteaServer { get; set; }

   /// <summary>
   /// The workspace that was open last: an account's id, or <see cref="StartWorkspace.QuickSync"/>.
   /// Null when nothing is recorded yet. See <see cref="StartWorkspace.Choose"/>.
   /// </summary>
   public Guid? LastWorkspace { get; set; }

   /// <summary>
   /// The forge Quick Sync last connected to, by name; null for GitHub. With
   /// <see cref="QuickSyncServer"/>, <see cref="QuickSyncOrganization"/>,
   /// <see cref="QuickSyncTargetFolder"/> and <see cref="QuickSyncCreateOrgSubfolder"/>
   /// it is what <see cref="RememberedQuickSync"/> gives back. No token is among them.
   /// </summary>
   public string? QuickSyncForge { get; set; }

   /// <summary>The address of the Gitea server Quick Sync last connected to; null for GitHub.</summary>
   public string? QuickSyncServer { get; set; }

   /// <summary>The organization Quick Sync last loaded; empty when none.</summary>
   public string QuickSyncOrganization { get; set; } = "";

   /// <summary>The target folder of Quick Sync's last connection; empty when none.</summary>
   public string QuickSyncTargetFolder { get; set; } = "";

   /// <summary>Whether Quick Sync's last connection cloned under an organization subfolder.</summary>
   public bool QuickSyncCreateOrgSubfolder { get; set; }

   /// <summary>Folder pre-filled as the sync target. Empty means "no default".</summary>
   public string DefaultTargetFolder { get; set; } = "";

   /// <summary>Default parallel clone/pull count; clamped to 1..64 on load and save.</summary>
   public int DefaultMaxConcurrency { get; set; } = DefaultConcurrency;

   /// <summary>App theme: "System", "Light", or "Dark". Unknown values fall back to "System".</summary>
   public string Theme { get; set; } = "System";

   /// <summary>Whether the branded splash overlay shows at startup.</summary>
   public bool ShowSplashScreen { get; set; } = true;

   /// <summary>How long the splash stays up before fading, clamped to 500..30000 ms.</summary>
   public int SplashMilliseconds { get; set; } = DefaultSplashMilliseconds;



   /// <summary>The settings file in <paramref name="directory"/>, or in the data folder when none is named.</summary>
   private static string SettingsPath(string? directory) => Path.Combine(directory ?? GcloPaths.DataRoot, "settings.json");



   /// <summary>
   /// Loads settings from disk. Never throws: a missing, corrupt, or unreadable file
   /// yields defaults, and loaded values are sanitized (concurrency clamped, theme validated).
   /// A file that is there but cannot be used is reported to <paramref name="log"/>.
   /// </summary>
   /// <param name="log">Where a file that cannot be used is reported.</param>
   /// <param name="directory">The folder that holds settings.json; the data folder (<see cref="GcloPaths.DataRoot"/>) when null.</param>
   public static AppSettings Load(IActivityLog? log = null, string? directory = null)
   {
      try
      {
         string path = SettingsPath(directory);
         if(File.Exists(path))
         {
            AppSettings? loaded = JsonSerializer.Deserialize(
                File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings);
            if(loaded is not null)
            {
               loaded.Sanitize();
               return loaded;
            }
         }
      }
      catch(Exception ex)
      {
         // Settings must never prevent startup; fall through to defaults.
         log?.Error($"Settings could not be loaded ({ex.Message}); the defaults are used.", ex);
         return new AppSettings { CouldNotBeRead = true };
      }

      return new AppSettings();
   }



   /// <summary>
   /// True when the settings file was there and could not be read, so these are the
   /// defaults in its place. What the app remembers by itself is then not written
   /// (<see cref="SaveWhatWasRemembered"/>): the file is left for the user to mend,
   /// as it was before the app remembered anything. Saving the Settings dialog
   /// still writes.
   /// </summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public bool CouldNotBeRead { get; private set; }



   /// <summary>True once this window recorded a Quick Sync connection itself (<see cref="RememberQuickSync"/>).</summary>
   private bool _rememberedAConnection;



   /// <summary>
   /// Writes what the app remembered by itself (the workspace that was open, and
   /// Quick Sync's connection if this window made one), not what the user saved in
   /// Settings. Only those values are written, over the file as it is on disk now:
   /// another gclo window may have saved settings since this one read them, and a
   /// window that closes does not put its older copy of them back. Does nothing
   /// when the file could not be read, at start or now: that file is not written
   /// over with defaults on the app's own account.
   /// </summary>
   public bool SaveWhatWasRemembered(IActivityLog? log = null, string? directory = null)
   {
      if(CouldNotBeRead)
      {
         return false;
      }

      AppSettings onDisk = Load(log: null, directory);
      if(onDisk.CouldNotBeRead)
      {
         log?.Info("What the app remembers was not written: the settings file cannot be read just now.");
         return false;
      }

      onDisk.LastWorkspace = LastWorkspace;
      if(_rememberedAConnection)
      {
         onDisk.QuickSyncForge = QuickSyncForge;
         onDisk.QuickSyncServer = QuickSyncServer;
         onDisk.QuickSyncOrganization = QuickSyncOrganization;
         onDisk.QuickSyncTargetFolder = QuickSyncTargetFolder;
         onDisk.QuickSyncCreateOrgSubfolder = QuickSyncCreateOrgSubfolder;
      }
      return onDisk.Save(log, directory);
   }



   /// <summary>How long a save tries to put the new file in place while something has the old one open.</summary>
   private static readonly TimeSpan s_savePatience = TimeSpan.FromSeconds(1);



   /// <summary>
   /// Persists settings to disk, creating the directory if needed. The new content is
   /// written beside the file and then put in its place in one step, so a crash or a
   /// full disk in the middle leaves the previous settings, never half a file. The
   /// step is refused while anything has the file open, so it is tried again for a
   /// moment. Failures (locked file, read-only profile, full disk, ...) do not throw,
   /// because losing a preference write must never crash the app; they are reported
   /// to <paramref name="log"/> and in the result.
   /// </summary>
   /// <param name="log">Where a failure is reported.</param>
   /// <param name="directory">The folder that holds settings.json; the data folder (<see cref="GcloPaths.DataRoot"/>) when null.</param>
   /// <returns>True when the settings are on disk.</returns>
   public bool Save(IActivityLog? log = null, string? directory = null) => Save(log, s_savePatience, directory);



   /// <summary>Saves with its own patience for a file that is in use, for tests.</summary>
   internal bool Save(IActivityLog? log, TimeSpan patience, string? directory = null)
   {
      try
      {
         Sanitize();
         string path = SettingsPath(directory);
         string? folder = Path.GetDirectoryName(path);
         if(!string.IsNullOrEmpty(folder))
         {
            Directory.CreateDirectory(folder);
         }

         // A name of its own: two processes saving at once do not write one file.
         string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
         try
         {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
            MoveIntoPlace(temporary, path, patience);
         }
         finally
         {
            // Gone already when the move succeeded; otherwise nothing is left behind.
            File.Delete(temporary);
         }
         return true;
      }
      catch(Exception ex)
      {
         log?.Error($"Settings could not be saved ({ex.Message}); the change is lost when the app closes.", ex);
         return false;
      }
   }



   private static void MoveIntoPlace(string temporary, string path, TimeSpan patience)
   {
      long started = System.Diagnostics.Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            File.Move(temporary, path, overwrite: true);
            return;
         }
         catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                                      && System.Diagnostics.Stopwatch.GetElapsedTime(started) < patience)
         {
            Thread.Sleep(15);
         }
      }
   }



   /// <summary>
   /// The Gitea server a default token is saved for, or null when there is none or
   /// what is stored is no address that can be used (a file edited by hand).
   /// </summary>
   public ForgeEndpoint? DefaultGiteaPlace()
       => ForgeEndpoint.TryCreate(ForgeKind.Gitea, DefaultGiteaServer, out ForgeEndpoint? place, out _) ? place : null;



   /// <summary>
   /// Records a Quick Sync connection that worked, so the card opens the same way
   /// next time. The caller saves. Never a token.
   /// </summary>
   public void RememberQuickSync(QuickSyncConnection connection)
   {
      ArgumentNullException.ThrowIfNull(connection);
      QuickSyncForge = connection.Place.Kind == ForgeKind.GitHub ? null : connection.Place.Kind.ToString();
      QuickSyncServer = connection.Place.Server?.AbsoluteUri;
      QuickSyncOrganization = connection.Organization.Trim();
      QuickSyncTargetFolder = connection.TargetFolder.Trim();
      QuickSyncCreateOrgSubfolder = connection.CreateOrgSubfolder;
      _rememberedAConnection = true;
   }



   /// <summary>
   /// The Quick Sync connection that was recorded. Null when none was (a first
   /// run: a connection that worked has an organization), and when what is stored
   /// names a place that cannot be used (a file edited by hand, or a forge of a
   /// later version). The card then opens with no place chosen.
   /// </summary>
   public QuickSyncConnection? RememberedQuickSync()
   {
      if(QuickSyncOrganization.Length == 0)
      {
         return null;
      }

      ForgeKind? kind = QuickSyncForge is null ? ForgeKind.GitHub
          : string.Equals(QuickSyncForge, nameof(ForgeKind.Gitea), StringComparison.OrdinalIgnoreCase) ? ForgeKind.Gitea
          : null;
      return kind is ForgeKind known && ForgeEndpoint.TryCreate(known, QuickSyncServer, out ForgeEndpoint? place, out _)
          ? new QuickSyncConnection(place, QuickSyncOrganization, QuickSyncTargetFolder, QuickSyncCreateOrgSubfolder)
          : null;
   }



   private void Sanitize()
   {
      // The JSON deserializer can assign null despite the non-nullable annotation.
      DefaultTargetFolder ??= "";
      QuickSyncOrganization ??= "";
      QuickSyncTargetFolder ??= "";
      DefaultMaxConcurrency = Math.Clamp(DefaultMaxConcurrency, MinConcurrency, MaxConcurrency);
      // A file written before this setting existed has no such field, and the
      // property then keeps its default; whatever a file does say is brought into range.
      SplashMilliseconds = Math.Clamp(SplashMilliseconds, MinSplashMilliseconds, MaxSplashMilliseconds);
      if(Theme is not ("System" or "Light" or "Dark"))
      {
         Theme = "System";
      }
   }
}
