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
   /// <summary>ASFW_ANY: any process may bring its window to the front.</summary>
   private const int AnyProcess = -1;



   /// <summary>
   /// This process's claim on the data folder (#216): held from here until the
   /// main window closes. Null only before <see cref="Main"/> has made it.
   /// </summary>
   internal static WindowClaim? Claim { get; private set; }



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

      /////////////////////////////////////////////////////////////////////////////
      // One window per data folder (#216). Another window on this folder would  //
      // keep its own copy of the settings and accounts and undo what this one   //
      // saves. The one that is open is asked to come to the front (this process //
      // was started by the user, so it may pass that right on), and this one    //
      // ends before any window or file of its own exists.                       //
      /////////////////////////////////////////////////////////////////////////////
      try
      {
         Claim = WindowClaim.For(GcloPaths.DataRoot);
      }
      catch(Exception)
      {
         // No log exists yet, and nothing here is worth not opening for: without a claim the window opens as it always did.
         Claim = null;
      }

      if(Claim is { IsTheOnlyOne: false })
      {
         _ = AllowSetForegroundWindow(AnyProcess);
         Claim.AskTheOtherToShowItself();
         Claim.Dispose();
         return;
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



   [System.Runtime.InteropServices.DllImport("user32.dll")]
   private static extern int AllowSetForegroundWindow(int processId);
}
