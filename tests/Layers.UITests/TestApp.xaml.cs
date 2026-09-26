using Microsoft.UI.Xaml;

namespace Layers.UITests;

/// Minimal app with the stock Fluent resources the windows use.
/// A XAML app (not a bare Application subclass), so the compiler generates the metadata provider that finds the
/// stock controls' and Layers.UI's XAML types, as it does for the real app.
public sealed partial class TestApp : Application
{
    public TestApp()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }
}
