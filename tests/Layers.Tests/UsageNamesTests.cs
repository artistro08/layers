using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class UsageNamesTests
{
    [TestMethod]
    [DataRow(0x00070004u, "A")]
    [DataRow(0x000700E0u, "Left Control")]
    [DataRow(0x00090001u, "Left button")]
    [DataRow(0x00090010u, "Button 16")]
    [DataRow(0x000C00E9u, "Volume up")]
    [DataRow(0xFFF79090u, "MIDI CH0 note 144")]
    [DataRow(0xFFF7B307u, "MIDI CH3 CC7")]
    [DataRow(0x12345678u, "0x12345678")]
    public void Input_MatchesWebTool(uint usage, string expected) => Assert.AreEqual(expected, UsageNames.Input(usage));

    [TestMethod]
    [DataRow(0x000C0224u, "Browser back")]
    [DataRow(0x000C0225u, "Browser forward")]
    [DataRow(0x000C0223u, "Browser home")]
    [DataRow(0x000C0227u, "Refresh")]
    [DataRow(0x000C006Fu, "Brightness up")]
    [DataRow(0x000C0192u, "Calculator")]
    public void Input_NamesMediaCodesTheWebToolLeavesAsHex(uint usage, string expected) => Assert.AreEqual(expected, UsageNames.Input(usage));

    [TestMethod]
    public void Input_TheWebToolsNamesStillWin() => Assert.AreEqual("Volume up", UsageNames.Input(0x000C00E9));

    [TestMethod]
    public void Output_NamesMediaCodesToo() => Assert.AreEqual("Browser back", UsageNames.Output(0x000C0224, 0));

    [TestMethod]
    [DataRow(0xFFF10002u, "Layer 2")]
    [DataRow(0xFFF20001u, "Macro 1")]
    [DataRow(0x00000000u, "Nothing")]
    [DataRow(0x00070004u, "A")]
    public void Output_UsesDescriptorTable(uint usage, string expected) => Assert.AreEqual(expected, UsageNames.Output(usage, 0));

    [TestMethod]
    public void Output_UnknownDescriptorFallsBackToZero() =>
        Assert.AreEqual(UsageNames.Output(0x00070004, 0), UsageNames.Output(0x00070004, 9));

    [TestMethod]
    public void Output_DescriptorOneIsDescriptorZero() =>
        Assert.AreEqual(UsageNames.Output(0x000C00E9, 0), UsageNames.Output(0x000C00E9, 1));

    [TestMethod]
    public void Hex_IsUppercaseEightDigits() => Assert.AreEqual("0x0007001E", UsageNames.Hex(0x0007001E));

    [TestMethod]
    public void Pages_AreRecognized()
    {
        Assert.IsTrue(UsageNames.IsMacro(0xFFF20005));
        Assert.IsTrue(UsageNames.IsLayer(0xFFF10001));
        Assert.IsFalse(UsageNames.IsMacro(0x00070004));
        Assert.IsFalse(UsageNames.IsLayer(0xFFF20005));
    }
}
