using System.Xml.Linq;

namespace Layers.Tests;

[TestClass]
public sealed class VersionSyncTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    [TestMethod]
    public void MsixVersion_MatchesProps()
    {
        var version  = XDocument.Load(Path.Combine(Root(), "Directory.Build.props")).Descendants("Version").First().Value;
        var manifest = XDocument.Load(Path.Combine(Root(), "src", "Layers", "Package.appxmanifest"));
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        Assert.AreEqual($"{version}.0", manifest.Root!.Element(ns + "Identity")!.Attribute("Version")!.Value);
    }
}
