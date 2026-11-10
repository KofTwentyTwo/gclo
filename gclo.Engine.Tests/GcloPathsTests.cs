/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Where gclo keeps its per-user data. The default must stay outside the Velopack
/// install root (%LOCALAPPDATA%\gclo): Setup.exe uninstalls an existing install
/// first and deletes that folder, which destroyed accounts.json and settings.json
/// in 1.0.2-beta.1 (#91).
/// </summary>
[Collection("GCLO_DATA_DIR")] // sets a process-wide variable: never beside another class that does
public sealed class GcloPathsTests
{
   /// <summary>The default root is %LOCALAPPDATA%\KofTwentyTwo\gclo.</summary>
   [Fact]
   public void DefaultDataRoot_IsLocalAppDataKofTwentyTwoGclo()
   {
      string expected = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "KofTwentyTwo", "gclo");

      Assert.Equal(expected, GcloPaths.DefaultDataRoot);
   }



   /// <summary>The dev edition keeps its own data root beside the stable one (#114).</summary>
   [Fact]
   public void DataFolderNameFor_DevEdition_UsesItsOwnFolder()
   {
      // Edition itself is process-wide state set once by the app's entry point; the
      // tests exercise the pure mapping so they cannot race the vault's scoping.
      Assert.Equal("gclo", GcloPaths.DataFolderNameFor(""));
      Assert.Equal("gclo-dev", GcloPaths.DataFolderNameFor("dev"));
      Assert.Throws<ArgumentNullException>(() => GcloPaths.DataFolderNameFor(null!));
      Assert.Equal("", GcloPaths.Edition);
      Assert.Equal("gclo", GcloPaths.DataFolderName);
   }



   /// <summary>The default root is never the install root nor anything under it.</summary>
   [Fact]
   public void DefaultDataRoot_IsNotTheVelopackInstallRoot()
   {
      string installRoot = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gclo");

      Assert.NotEqual(installRoot, GcloPaths.DefaultDataRoot, StringComparer.OrdinalIgnoreCase);
      Assert.False(
          GcloPaths.DefaultDataRoot.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
   }



   /// <summary>The log folder follows the data folder, so the app and the CLI log side by side wherever that is (#159).</summary>
   [Fact]
   public void LogDirectory_IsLogsUnderTheDataRoot_AlsoWhenTheDataRootIsMoved()
   {
      string moved = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
      Environment.SetEnvironmentVariable("GCLO_DATA_DIR", moved);
      try
      {
         Assert.Equal(Path.Combine(moved, "logs"), GcloPaths.LogDirectory);
      }
      finally
      {
         Environment.SetEnvironmentVariable("GCLO_DATA_DIR", null);
      }

      Assert.Equal(Path.Combine(GcloPaths.DefaultDataRoot, "logs"), GcloPaths.LogDirectory);
   }
}
