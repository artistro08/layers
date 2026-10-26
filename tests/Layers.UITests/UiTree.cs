using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Layers.UITests;

/// Visual-tree helpers shared by the UI tests: open a control in a throwaway window, walk its descendants, click buttons.
internal static class UiTree
{
    /// Opens a bare window around the control so templates apply and layout runs. Close it when done.
    public static Window ShowInWindow(UIElement content)
    {
        var window = new Window { Content = content };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 600));
        window.Activate();
        return window;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<T> FindDescendants<T>(this DependencyObject root, Func<T, bool>? where = null)
        where T : DependencyObject =>
        Descendants(root).OfType<T>().Where(item => where is null || where(item));

    public static T? FindDescendant<T>(this DependencyObject root, Func<T, bool>? where = null)
        where T : DependencyObject =>
        root.FindDescendants(where).FirstOrDefault();

    /// Clicks a button the way a screen reader would.
    public static void Invoke(ButtonBase button) =>
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button)).Invoke();

    /// Clicks a menu item the way a screen reader would.
    public static void Invoke(MenuFlyoutItem item) =>
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item)).Invoke();
}
