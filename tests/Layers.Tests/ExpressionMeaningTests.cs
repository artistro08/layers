using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class ExpressionMeaningTests
{
    private static List<IReadOnlyList<IReadOnlyList<uint>>> EmptyMacros() =>
        Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList();

    // =========================================================================
    // PARSE
    // =========================================================================

    [TestMethod]
    public void Parse_SplitsTextAndKeys()
    {
        var parts = ExpressionMeaning.Parse("Hold {Back} and swipe {Left arrow}");

        CollectionAssert.AreEqual(new[]
        {
            new MeaningPart("Hold", false),
            new MeaningPart("Back", true),
            new MeaningPart("and swipe", false),
            new MeaningPart("Left arrow", true),
        }, parts.ToArray());
    }

    [TestMethod]
    public void Parse_KeysOnly() =>
        CollectionAssert.AreEqual(new[] { new MeaningPart("Left GUI", true), new MeaningPart("T", true) }, ExpressionMeaning.Parse("{Left GUI}{T}").ToArray());

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_BlankIsEmpty(string? text) => Assert.IsEmpty(ExpressionMeaning.Parse(text));

    [TestMethod]
    public void Parse_UnclosedOrEmptyBracesStayText()
    {
        CollectionAssert.AreEqual(new[] { new MeaningPart("Scroll {up", false) }, ExpressionMeaning.Parse("Scroll {up").ToArray());
        CollectionAssert.AreEqual(new[] { new MeaningPart("a {} b", false) }, ExpressionMeaning.Parse("a {} b").ToArray());
    }

    [TestMethod]
    public void Token_WrapsTheName() => Assert.AreEqual("{Volume up}", ExpressionMeaning.Token("Volume up"));

    // =========================================================================
    // SLOTS
    // =========================================================================

    [TestMethod]
    [DataRow("Expression 1", 1)]
    [DataRow("Expression 8", 8)]
    public void Slot_ReadsTheNumber(string name, int slot) => Assert.AreEqual(slot, ExpressionMeaning.Slot(name));

    [TestMethod]
    [DataRow("Expression 9")]
    [DataRow("Expression")]
    [DataRow("Layer 2")]
    public void Slot_OtherNamesAreNull(string name) => Assert.IsNull(ExpressionMeaning.Slot(name));

    [TestMethod]
    public void Uses_ListsMappedExpressionsWithWhatTheyDrive()
    {
        // Expression 1 Drives Forward, Expression 2 Drives V Scroll On Two Layers, Expression 5 Is Driven By A Key
        var config = new DeviceConfig(0,
        [
            new Mapping(0xFFF30001, 0x00090005, 1, false, false, false),
            new Mapping(0xFFF30002, 0x00010038, 0b10, false, false, false),
            new Mapping(0xFFF30002, 0x00010038, 0b100, false, false, false),
            new Mapping(0x00070004, 0xFFF30005, 1, false, false, false),
        ], EmptyMacros());

        var uses = ExpressionMeaning.Uses(config);

        CollectionAssert.AreEqual(new[] { 1, 2, 5 }, uses.Select(u => u.Slot).ToArray());
        CollectionAssert.AreEqual(new[] { "Forward" }, uses[0].Drives.ToArray());
        CollectionAssert.AreEqual(new[] { "V scroll" }, uses[1].Drives.ToArray(), "listed once");
        Assert.IsEmpty(uses[2].Drives);
    }

    [TestMethod]
    public void Uses_SkipsMaskZero()
    {
        var config = new DeviceConfig(0, [new Mapping(0xFFF30003, 0x00090005, 0, false, false, false)], EmptyMacros());

        Assert.IsEmpty(ExpressionMeaning.Uses(config));
    }

    // =========================================================================
    // KEY NAMES
    // =========================================================================

    [TestMethod]
    public void AllNames_HasKeysMouseMediaLayersAndMacrosOnce()
    {
        var names = UsageNames.AllNames;

        CollectionAssert.IsSubsetOf(new[] { "A", "Left GUI", "Left button", "Volume up", "Layer 2", "Macro 1", "V scroll" }, names.ToList());
        CollectionAssert.DoesNotContain(names.ToList(), "Nothing");
        Assert.HasCount(names.Distinct().Count(), names);
    }
}
