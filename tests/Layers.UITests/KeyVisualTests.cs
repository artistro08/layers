using Layers.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

[TestClass]
public sealed class KeyVisualTests
{
    [TestMethod]
    public Task KeyVisual_RendersText() => UiHost.RunAsync(async () =>
    {
        var visual = new KeyVisual { Content = "PrintScreen", Style = (Style)Application.Current.Resources["KeyChipStyle"] };
        var host   = UiTree.ShowInWindow(visual);
        await UiHost.Settle();

        Assert.IsNotNull(visual.FindDescendant<TextBlock>(t => t.Text == "PrintScreen"));
        host.Close();
    });

    [TestMethod]
    public Task KeyVisual_WindowsKeyIsLogo() => UiHost.RunAsync(async () =>
    {
        var visual = new KeyVisual { Content = 92 };
        var host   = UiTree.ShowInWindow(visual);
        await UiHost.Settle();

        Assert.IsNotNull(visual.FindDescendant<Viewbox>());
        Assert.IsNotNull(visual.FindDescendant<PathIcon>());
        host.Close();
    });

    [TestMethod]
    public Task KeyVisual_ShiftIsGlyphOnlyWhenAsked() => UiHost.RunAsync(async () =>
    {
        var text  = new KeyVisual { Content = 16 };
        var glyph = new KeyVisual { Content = 16, RenderKeyAsGlyph = true };
        var panel = new StackPanel { Children = { text, glyph } };
        var host  = UiTree.ShowInWindow(panel);
        await UiHost.Settle();

        Assert.IsNotNull(text.FindDescendant<TextBlock>(t => t.Text == "Shift"));
        Assert.IsNotNull(glyph.FindDescendant<FontIcon>(f => f.Glyph == ""));
        host.Close();
    });

    [TestMethod]
    public Task KeyVisual_StateChangesVisualState() => UiHost.RunAsync(async () =>
    {
        var visual = new KeyVisual { Content = "A", State = KeyVisualState.Warning };
        var host   = UiTree.ShowInWindow(visual);
        await UiHost.Settle();

        var holder = visual.FindDescendant<Grid>(g => g.Name == "KeyHolder")!;
        Assert.AreEqual(Application.Current.Resources["SystemFillColorCautionBackgroundBrush"], holder.Background);
        host.Close();
    });
}
