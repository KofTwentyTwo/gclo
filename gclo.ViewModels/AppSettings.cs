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



   private static string SettingsPath => Path.Combine(GcloPaths.DataRoot, "settings.json");



   /// <summary>
   /// Loads settings from disk. Never throws: a missing, corrupt, or unreadable file
   /// yields defaults, and loaded values are sanitized (concurrency clamped, theme validated).
   /// A file that is there but cannot be used is reported to <paramref name="log"/>.
   /// </summary>
   public static AppSettings Load(IActivityLog? log = null)
   {
      try
      {
         string path = SettingsPath;
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
      }

      return new AppSettings();
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
   /// <returns>True when the settings are on disk.</returns>
   public bool Save(IActivityLog? log = null) => Save(log, s_savePatience);



   /// <summary>Saves with its own patience for a file that is in use, for tests.</summary>
   internal bool Save(IActivityLog? log, TimeSpan patience)
   {
      try
      {
         Sanitize();
         string path = SettingsPath;
         string? directory = Path.GetDirectoryName(path);
         if(!string.IsNullOrEmpty(directory))
         {
            Directory.CreateDirectory(directory);
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



   private void Sanitize()
   {
      // The JSON deserializer can assign null despite the non-nullable annotation.
      DefaultTargetFolder ??= "";
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
