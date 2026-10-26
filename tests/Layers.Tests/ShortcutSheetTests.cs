using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class ShortcutSheetTests
{
    private static List<IReadOnlyList<IReadOnlyList<uint>>> EmptyMacros() =>
        Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList();

    private static DeviceConfig Config(params Mapping[] mappings) => new(0, mappings, EmptyMacros());

    [TestMethod]
    public void Build_OneSectionPerLayerInOrder()
    {
        var sections = ShortcutSheet.Build(Config(
            new Mapping(0x00070004, 0x000C00E9, 0b0101, false, false, false),
            new Mapping(0x00070005, 0xFFF10002, 0b0001, false, false, false)));

        CollectionAssert.AreEqual(new[] { 0, 2 }, sections.Select(s => s.Layer).ToArray());
        Assert.AreEqual("Layer 0", sections[0].Title);
        Assert.HasCount(2, sections[0].Keys);
        Assert.HasCount(1, sections[1].Keys);
    }

    [TestMethod]
    public void Build_AKeysMappingsOnALayerSitTogether()
    {
        // Right Button Tap And Hold On Layer 0, With Button 6 Listed Between Them On The Device
        var keys = ShortcutSheet.Build(Config(
            new Mapping(0x00090002, 0xFFF2000D, 0b0001, false, true, false),
            new Mapping(0x00090006, 0x000C00E9, 0b0001, false, false, false),
            new Mapping(0x00090002, 0xFFF10002, 0b0001, false, false, true)))[0].Keys;

        CollectionAssert.AreEqual(new[] { "Right button", "Button 6" }, keys.Select(k => k.Input).ToArray());
        Assert.HasCount(2, keys[0].Rows, "tap and hold sit under the one key");
        Assert.IsTrue(keys[0].Rows[0].Tap);
        Assert.IsTrue(keys[0].Rows[1].Hold);
    }

    [TestMethod]
    public void Build_SkipsMaskZero() =>
        Assert.IsEmpty(ShortcutSheet.Build(Config(new Mapping(1, 2, 0, false, false, false))));

    [TestMethod]
    public void Build_FlagsCopied()
    {
        var row = ShortcutSheet.Build(Config(new Mapping(1, 2, 1, true, false, true)))[0].Keys[0].Rows[0];

        Assert.IsTrue(row.Sticky);
        Assert.IsFalse(row.Tap);
        Assert.IsTrue(row.Hold);
    }

    [TestMethod]
    public void Build_MacroRowNamesSteps()
    {
        var macros = EmptyMacros();
        macros[2]  = [[0x000700E0u, 0x00070006u], [0x00070019u]];
        var config = new DeviceConfig(0, [new Mapping(0x00070004, 0xFFF20003, 1, false, false, false)], macros);
        var output = (MacroOutput)ShortcutSheet.Build(config)[0].Keys[0].Rows[0].Output;

        Assert.AreEqual(3, output.Number);
        Assert.HasCount(2, output.Steps);
        CollectionAssert.AreEqual(new[] { "Left Control", "C" }, output.Steps[0].ToArray());
        CollectionAssert.AreEqual(new[] { "V" }, output.Steps[1].ToArray());
    }

    [TestMethod]
    public void Build_EmptyMacroHasNoSteps()
    {
        var output = (MacroOutput)ShortcutSheet.Build(Config(new Mapping(1, 0xFFF20001, 1, false, false, false)))[0].Keys[0].Rows[0].Output;

        Assert.AreEqual(1, output.Number);
        Assert.IsEmpty(output.Steps);
    }

    [TestMethod]
    public void Build_MacroPastTheSlotsIsPlainKeys()
    {
        // 0xFFF20021 Would Be Macro 33: The Device Has 32, So It Is Shown By Name Only
        var output = ShortcutSheet.Build(Config(new Mapping(1, 0xFFF20021, 1, false, false, false)))[0].Keys[0].Rows[0].Output;

        Assert.IsInstanceOfType<KeysOutput>(output);
    }

    [TestMethod]
    public void Build_SkipsMappingsToNothing()
    {
        // A Key Silenced On A Layer (So An Expression Can Read It) Does Nothing There, So It Isn't Listed
        var sections = ShortcutSheet.Build(Config(
            new Mapping(0x000C00E9, 0, 0b10, false, false, false),
            new Mapping(0x00070004, 0x00070005, 0b10, false, false, false)));

        CollectionAssert.AreEqual(new[] { "A" }, sections.Single().Keys.Select(k => k.Input).ToArray());
    }

    [TestMethod]
    public void Build_UsesDescriptorForOutputs()
    {
        var config = new DeviceConfig(2, [new Mapping(1, 0x00070004, 1, false, false, false)], EmptyMacros());

        Assert.AreEqual(UsageNames.Output(0x00070004, 2), ((KeysOutput)ShortcutSheet.Build(config)[0].Keys[0].Rows[0].Output).Keys[0]);
    }
}
