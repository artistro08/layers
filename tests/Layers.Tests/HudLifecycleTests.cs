using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HudLifecycleTests
{
    [TestMethod]
    public void Show_OpensOnceThenUpdatesInPlace()
    {
        var hud = new HudLifecycle();

        Assert.IsTrue(hud.Show());
        Assert.IsFalse(hud.Show());
    }

    [TestMethod]
    public void HideThenClosed_HidesHostAndNextShowOpens()
    {
        var hud = new HudLifecycle();
        hud.Show();

        Assert.IsTrue(hud.Hide());
        Assert.IsFalse(hud.Closed());
        Assert.IsTrue(hud.Show());
    }

    [TestMethod]
    public void ShowWhileClosing_ReopensOnClosed()
    {
        var hud = new HudLifecycle();
        hud.Show();
        hud.Hide();

        Assert.IsFalse(hud.Show());
        Assert.IsTrue(hud.Closed());

        // Open Again, So A Later Show Only Updates
        Assert.IsFalse(hud.Show());
        Assert.IsTrue(hud.Hide());
        Assert.IsFalse(hud.Closed());
    }

    [TestMethod]
    public void Hide_WhenNotOpenOrAlreadyClosing_DoesNothing()
    {
        var hud = new HudLifecycle();

        Assert.IsFalse(hud.Hide());
        hud.Show();
        Assert.IsTrue(hud.Hide());
        Assert.IsFalse(hud.Hide());
    }

    [TestMethod]
    public void ClosedOnItsOwn_NextShowOpens()
    {
        var hud = new HudLifecycle();
        hud.Show();

        Assert.IsFalse(hud.Closed());
        Assert.IsTrue(hud.Show());
    }

    [TestMethod]
    public void Lost_WhenOpenButFlyoutGone_ResetsSoNextShowOpens()
    {
        var hud = new HudLifecycle();
        hud.Show();

        Assert.IsFalse(hud.Lost(isOpen: true));
        Assert.IsTrue(hud.Lost(isOpen: false));
        Assert.IsTrue(hud.Show());
    }

    [TestMethod]
    public void Lost_IgnoredWhileClosingOrClosed()
    {
        var hud = new HudLifecycle();
        Assert.IsFalse(hud.Lost(isOpen: false));

        hud.Show();
        hud.Hide();
        hud.Show();

        // A Closing Flyout Reads As Not Open, And Closed Still Owes The Reopen
        Assert.IsFalse(hud.Lost(isOpen: false));
        Assert.IsTrue(hud.Closed());
    }

    [TestMethod]
    public void Lost_AfterOpened_CountsAsClosingSoShowReopensOnLateClosed()
    {
        var hud = new HudLifecycle();
        hud.Show();
        hud.Opened();

        // Closed On Its Own: IsOpen Is Already False, Closed Hasn't Fired Yet
        Assert.IsFalse(hud.Lost(isOpen: false));
        Assert.IsFalse(hud.Show());

        // The Late Closed Reopens Instead Of Hiding The Host
        Assert.IsTrue(hud.Closed());
    }

    [TestMethod]
    public void Lost_AfterOpened_LateClosedWithoutShowHidesHost()
    {
        var hud = new HudLifecycle();
        hud.Show();
        hud.Opened();

        Assert.IsFalse(hud.Lost(isOpen: false));
        Assert.IsFalse(hud.Hide());
        Assert.IsFalse(hud.Closed());
        Assert.IsTrue(hud.Show());
    }

    [TestMethod]
    public void Closed_ForgetsOpened_SoAFailedReopenStillResets()
    {
        var hud = new HudLifecycle();
        hud.Show();
        hud.Opened();
        hud.Hide();
        hud.Show();

        // Reopen Requested, But That ShowAt Never Opens
        Assert.IsTrue(hud.Closed());
        Assert.IsTrue(hud.Lost(isOpen: false));
        Assert.IsTrue(hud.Show());
    }
}
