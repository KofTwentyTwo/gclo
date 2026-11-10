/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>Pins defaults and load-time sanitizing of <see cref="AppSettings"/>.</summary>
public sealed class AppSettingsTests
{
   [Fact]
   public void Defaults_SplashOnAtFiveSeconds()
   {
      var settings = new AppSettings();

      Assert.True(settings.ShowSplashScreen);
      Assert.Equal(5_000, settings.SplashMilliseconds);
   }



   [Fact]
   public void Load_SettingsFileFromOlderVersion_MigratesSplashToDefault()
   {
      // A pre-splash settings.json has no SplashMilliseconds field; it must load
      // as the 5s default, not as 0 clamped to the minimum.
      string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
      try
      {
         Directory.CreateDirectory(dir);
         File.WriteAllText(
             Path.Combine(dir, "settings.json"),
             """{ "DefaultTargetFolder": "C:\\src", "DefaultMaxConcurrency": 8, "Theme": "Dark" }""");

         var loaded = AppSettings.Load(directory: dir);

         Assert.Equal(AppSettings.DefaultSplashMilliseconds, loaded.SplashMilliseconds);
         Assert.True(loaded.ShowSplashScreen);
         Assert.Equal("Dark", loaded.Theme); // existing values still honored
      }
      finally
      {
         try
         {
            Directory.Delete(dir, recursive: true);
         }
         catch(IOException)
         {
         }
      }
   }



   [Fact]
   public void Save_ClampsSplashDuration()
   {
      string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
      try
      {
         var settings = new AppSettings { SplashMilliseconds = 999_999 };
         settings.Save(directory: dir);
         Assert.Equal(AppSettings.MaxSplashMilliseconds, settings.SplashMilliseconds);

         settings.SplashMilliseconds = 1;
         settings.Save(directory: dir);
         Assert.Equal(AppSettings.MinSplashMilliseconds, settings.SplashMilliseconds);

         // Round trip: the persisted values load back as saved.
         var loaded = AppSettings.Load(directory: dir);
         Assert.Equal(AppSettings.MinSplashMilliseconds, loaded.SplashMilliseconds);
         Assert.True(loaded.ShowSplashScreen);
      }
      finally
      {
         try
         {
            Directory.Delete(dir, recursive: true);
         }
         catch(IOException)
         {
         }
      }
   }



   // ---------------------------------------------------------------- written whole; failures are said (#155)

   private static void WithDataDirectory(Action<string> test)
   {
      string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
         test(dir);
      }
      finally
      {
         GitTestHelpers.TryDeleteDirectory(dir);
      }
   }



   [Fact]
   public void Save_SaysItSucceeded_AndLeavesOnlyTheSettingsFile()
       => WithDataDirectory(dir =>
       {
          var log = new RecordingActivityLog();

          bool saved = new AppSettings { Theme = "Dark" }.Save(log, dir);

          Assert.True(saved);
          Assert.Equal(["settings.json"], Directory.GetFiles(dir).Select(Path.GetFileName), StringComparer.Ordinal);
          Assert.Equal("Dark", AppSettings.Load(log, dir).Theme);
          Assert.Empty(log.Entries);
       });



   [Fact]
   public void Save_WhenItCannotPutTheFileInPlace_SaysSo_LeavesThePreviousSettings_AndNoTemporaryFile()
       => WithDataDirectory(dir =>
       {
          if(!OperatingSystem.IsWindows())
          {
             return; // elsewhere a file that is open can still be replaced
          }
          var log = new RecordingActivityLog();
          Assert.True(new AppSettings { Theme = "Dark" }.Save(directory: dir));
          string path = Path.Combine(dir, "settings.json");

          bool saved;
          using(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
          {
             saved = new AppSettings { Theme = "Light" }.Save(log, TimeSpan.FromMilliseconds(100), dir);
          }

          Assert.False(saved);
          (string level, string message) = Assert.Single(log.Entries);
          Assert.Equal("ERROR", level);
          Assert.StartsWith("Settings could not be saved (", message);
          Assert.Equal("Dark", AppSettings.Load(directory: dir).Theme); // not half a file, not the new one: the old one
          Assert.Equal(["settings.json"], Directory.GetFiles(dir).Select(Path.GetFileName), StringComparer.Ordinal);
       });



   [Fact]
   public async Task Save_WhileTheFileIsOpenForAMoment_TriesAgain()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // elsewhere a file that is open can still be replaced
      }
      string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
         Assert.True(new AppSettings { Theme = "Dark" }.Save(directory: dir));
         Task<bool> save;
         using(new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
         {
            save = Task.Run(() => new AppSettings { Theme = "Light" }.Save(log: null, TimeSpan.FromSeconds(60), dir));
            Task finished = await Task.WhenAny(save, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(save, finished); // still trying
         }

         Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(60)));
         Assert.Equal("Light", AppSettings.Load(directory: dir).Theme);
      }
      finally
      {
         GitTestHelpers.TryDeleteDirectory(dir);
      }
   }



   [Fact]
   public void Save_ByTwoAtOnce_NeverLeavesHalfAFile_OrALeftover()
       => WithDataDirectory(dir =>
       {
          Parallel.For(0, 40, i => new AppSettings { Theme = i % 2 == 0 ? "Dark" : "Light" }.Save(directory: dir));

          Assert.Contains(AppSettings.Load(directory: dir).Theme, new[] { "Dark", "Light" }, StringComparer.Ordinal);
          Assert.Equal(["settings.json"], Directory.GetFiles(dir).Select(Path.GetFileName), StringComparer.Ordinal);
       });



   [Fact]
   public void Load_AFileThatCannotBeUsed_GivesTheDefaults_AndSaysSo()
       => WithDataDirectory(dir =>
       {
          var log = new RecordingActivityLog();
          File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");

          var loaded = AppSettings.Load(log, dir);

          Assert.Equal("System", loaded.Theme);
          (string level, string message) = Assert.Single(log.Entries);
          Assert.Equal("ERROR", level);
          Assert.StartsWith("Settings could not be loaded (", message);
       });



   [Fact]
   public void Load_ASplashDurationOfZero_IsBroughtIntoRange_LikeAnyOtherValue()
       => WithDataDirectory(dir =>
       {
          File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"SplashMilliseconds\": 0 }");

          Assert.Equal(AppSettings.MinSplashMilliseconds, AppSettings.Load(directory: dir).SplashMilliseconds);
       });
}
