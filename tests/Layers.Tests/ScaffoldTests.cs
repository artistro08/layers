using Layers.Core.Services;

namespace Layers.Tests;

[TestClass]
public sealed class ScaffoldTests
{
    [TestMethod]
    public void TestHost_IsUnpackaged()
    {
        Assert.IsFalse(AppPackaging.IsPackaged);
    }
}
