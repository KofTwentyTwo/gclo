using System;
using Velopack;

namespace gclo
{
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
        static void Main(string[] args)
        {
            // Must be first: handles Velopack install/update/uninstall hooks and may exit.
            VelopackApp.Build().Run();

            // The remainder mirrors the XAML-generated Main (obj\...\App.g.i.cs) verbatim.
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}
