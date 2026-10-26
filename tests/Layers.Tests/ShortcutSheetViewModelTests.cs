using Layers.Core.Logic;
using Layers.Core.ViewModels;

namespace Layers.Tests;

[TestClass]
public sealed class ShortcutSheetViewModelTests
{
    private static List<IReadOnlyList<IReadOnlyList<uint>>> EmptyMacros() =>
        Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList();

    private static DeviceConfig SomeConfig() => new(0, [new Mapping(0x00070004, 0x000C00E9, 0b11, false, false, false)], EmptyMacros());

    private static DeviceConfig EmptyConfig() => new(0, [], EmptyMacros());

    [TestMethod]
    public void Disconnected_ShowsPlaceholder()
    {
        var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = DeviceState.Initial };

        Assert.AreEqual("Plug in your HID Remapper", vm.EmptyMessage);
        Assert.IsTrue(vm.HasEmptyMessage);
        Assert.IsEmpty(vm.Sections);
        Assert.IsFalse(vm.ShowList);
        Assert.AreEqual("Disconnected", vm.StatusLabel);
        Assert.AreEqual(StatusTone.Critical, vm.Tone);
    }

    [TestMethod]
    public void VersionMismatch_ShowsDetailAndReadsNothing()
    {
        var vm = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.VersionMismatch, LayerMask.Base) };

        Assert.AreEqual("This app supports config version 18", vm.EmptyMessage);
        Assert.AreEqual(StatusTone.Caution, vm.Tone);
    }

    [TestMethod]
    public void NoSlot_ListsWithoutActiveMark()
    {
        var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.NoSlot, LayerMask.Base) };

        Assert.IsTrue(vm.Sections.Count > 0);
        Assert.IsFalse(vm.MarksActive, "the layer is unknown without a slot");
        Assert.IsTrue(vm.ShowList);
        Assert.IsNull(vm.EmptyMessage);
    }

    [TestMethod]
    public void Connected_EmptyConfigSaysNoShortcuts()
    {
        var vm = new ShortcutSheetViewModel { Config = EmptyConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };

        Assert.AreEqual("No shortcuts set up", vm.EmptyMessage);
    }

    [TestMethod]
    public void Connected_BeforeFirstReadShowsNothing()
    {
        var vm = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };

        Assert.IsNull(vm.EmptyMessage);
        Assert.IsFalse(vm.ShowList);
    }

    [TestMethod]
    public void Connected_MarksActiveLayer()
    {
        var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b10)) };

        Assert.IsTrue(vm.MarksActive);
        Assert.AreEqual(new LayerMask(0b10), vm.ActiveLayers);
        Assert.IsFalse(vm.HasEmptyMessage);
        Assert.AreEqual("Connected", vm.StatusLabel);
        Assert.AreEqual(StatusTone.Success, vm.Tone);
    }

    [TestMethod]
    public void StateChange_RaisesSections()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig() };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b10));

        CollectionAssert.IsSubsetOf(new[] { "Sections", "EmptyMessage", "StatusLabel", "Tone", "ShowList" }, raised);
    }

    [TestMethod]
    public void ConfigChange_RaisesSections()
    {
        var vm     = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Config = SomeConfig();

        CollectionAssert.IsSubsetOf(new[] { "Sections", "EmptyMessage", "ShowList" }, raised);
    }

    [TestMethod]
    public void LayerOnlyChange_RaisesActiveLayersNotSections()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b10));

        CollectionAssert.Contains(raised, "ActiveLayers");
        CollectionAssert.DoesNotContain(raised, "Sections");
        Assert.AreEqual(new LayerMask(0b10), vm.ActiveLayers);
    }

    [TestMethod]
    public void NoSlotToConnected_RaisesMarksActive()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.NoSlot, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.State = new DeviceState(DeviceStatus.Connected, LayerMask.Base);

        CollectionAssert.Contains(raised, "MarksActive");
        Assert.IsTrue(vm.MarksActive);
    }

    [TestMethod]
    public void StatusChange_StillRaisesSections()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.NoSlot, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.State = new DeviceState(DeviceStatus.Connected, LayerMask.Base);

        CollectionAssert.Contains(raised, "Sections");
    }

    [TestMethod]
    public void Connected_FailedFirstReadSaysSo()
    {
        var vm = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.Connected, LayerMask.Base), RefreshFailed = true };

        Assert.AreEqual("Couldn't read shortcuts", vm.EmptyMessage);
        Assert.IsTrue(vm.HasEmptyMessage);
        Assert.IsFalse(vm.ShowRefreshNote, "the header note is for a list that exists");
    }

    [TestMethod]
    public void Connected_FailedRefreshWithListShowsNote()
    {
        var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base), RefreshFailed = true };

        Assert.IsNull(vm.EmptyMessage);
        Assert.IsTrue(vm.ShowRefreshNote);
    }

    [TestMethod]
    public void Meanings_RaiseSections()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Meanings = new Dictionary<int, string> { [1] = "Scroll" };

        CollectionAssert.Contains(raised, "Sections");
        Assert.AreEqual("Scroll", vm.Meanings[1]);
    }

    [TestMethod]
    public void SameConfigAgain_DoesNotRebuild()
    {
        // Every Open Re-Reads The Device; An Identical Read Must Not Redraw Every Chip
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
        var before = vm.Sections;
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Config = SomeConfig();

        CollectionAssert.DoesNotContain(raised, "Sections");
        Assert.AreSame(before, vm.Sections);
    }

    [TestMethod]
    public void ChangedConfig_Rebuilds()
    {
        var vm     = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Config = EmptyConfig();

        CollectionAssert.Contains(raised, "Sections");
    }

    [TestMethod]
    public void RefreshFailed_Raises()
    {
        var vm     = new ShortcutSheetViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == "RefreshFailed";

        vm.RefreshFailed = true;

        Assert.IsTrue(raised && vm.RefreshFailed);
    }
}
