/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using gclo.ViewModels;
using Velopack;


namespace gclo;


/// <summary>
/// Custom entry point. The XAML-generated Main is disabled (DISABLE_XAML_GENERATED_MAIN
/// in gclo.csproj) so Velopack can run before any UI exists: during install, update, and
/// uninstall Velopack launches the exe with special arguments, handles them in
/// <c>VelopackApp.Run</c>, and may exit the process. In non-Velopack contexts (F5,
/// loose builds, MSIX packages) that call is inert and the app starts normally.
/// </summary>
public static class Program
{
   [STAThread]
   private static void Main()
   {
      // Must be first: handles Velopack install/update/uninstall hooks and may exit.
      VelopackApp.Build().Run();

      // The dev channel is packed as its own app (id gclo-dev) so it installs beside
      // the stable one; it keeps its own data root and says so in the title (#114).
      // Local and CLI runs have no Velopack app id and stay on the stable paths.
      if(string.Equals(Velopack.Locators.VelopackLocator.Current?.AppId, "gclo-dev", StringComparison.Ordinal))
      {
         GcloPaths.Edition = "dev";
      }

      // The remainder mirrors the XAML-generated Main (obj\...\App.g.i.cs) verbatim.
      global::WinRT.ComWrappersSupport.InitializeComWrappers();
      global::Microsoft.UI.Xaml.Application.Start((p) =>
      {
         var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
         global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
         _ = new App();
      });
   }
}
