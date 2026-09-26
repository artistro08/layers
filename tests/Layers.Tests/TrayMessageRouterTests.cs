using Layers.Core.Services;

namespace Layers.Tests;

[TestClass]
public sealed class TrayMessageRouterTests
{
    private const uint TaskbarCreated = 0xC123;

    [TestMethod]
    public void CallbackMessage_IsWmAppPlusOne()
    {
        // MSTEST0032: both sides resolve to the same compile-time literal, which is the point of this test
#pragma warning disable MSTEST0032
        Assert.AreEqual(0x8001u, TrayMessageRouter.TrayCallbackMessage);
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    [DataRow(0x0202)] // WM_LBUTTONUP
    [DataRow(0x0205)] // WM_RBUTTONUP
    public void Route_ButtonUpOpensMenu(int mouseMessage)
    {
        Assert.AreEqual(TrayAction.OpenMenu, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, mouseMessage, TaskbarCreated));
    }

    [TestMethod]
    [DataRow(0x0201)] // WM_LBUTTONDOWN
    [DataRow(0x0204)] // WM_RBUTTONDOWN
    [DataRow(0x0200)] // WM_MOUSEMOVE
    [DataRow(0x0203)] // WM_LBUTTONDBLCLK
    public void Route_OtherTrayMessagesIgnored(int mouseMessage)
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, mouseMessage, TaskbarCreated));
    }

    [TestMethod]
    public void Route_TaskbarCreatedReAdds()
    {
        Assert.AreEqual(TrayAction.ReAddIcon, TrayMessageRouter.Route(TaskbarCreated, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_UnregisteredTaskbarCreatedNeverMatchesZero()
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(0, 0, 0));
    }

    [TestMethod]
    [DataRow(0x001Au)] // WM_SETTINGCHANGE
    [DataRow(0x02E0u)] // WM_DPICHANGED
    public void Route_SettingAndDpiChangesRefresh(uint message)
    {
        Assert.AreEqual(TrayAction.Refresh, TrayMessageRouter.Route(message, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_CloseQuits()
    {
        // The MSI's CloseApplication and a plain taskkill send WM_CLOSE to top-level windows
        Assert.AreEqual(TrayAction.Quit, TrayMessageRouter.Route(0x0010, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_UnrelatedIgnored()
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(0x000F, 0, TaskbarCreated));
    }
}
