using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;

namespace Layers;

/// <summary>
/// Entry point.
/// </summary>
/// <remarks>
/// Replaces the XAML-generated <c>Main</c> so the single-instance check runs before any UI exists. A second launch
/// exits silently, as in Layers 1.0.3.
/// </remarks>
internal static class Program
{
    private const string InstanceKey = "LayersTrayApp";

    [STAThread]
    private static int Main()
    {
        // Keep The Current Directory Out Of DLL Search
        // TODO: SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) would also drop PATH, but under it the self-contained
        // unpackaged WinUI build can't load ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml and fails fast (0xC000027B)
        PInvoke.SetDllDirectory(string.Empty);

        // Single Instance
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            return 0;
        }

        // Run The App
        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
