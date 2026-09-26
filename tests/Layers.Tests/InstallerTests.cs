using System.Xml.Linq;

namespace Layers.Tests;

/// Inspects the built MSI's tables. Run by build.ps1 after the MSI is built.
[TestClass]
[TestCategory("Packaging")]
public sealed class InstallerTests
{
    private static dynamic s_database = null!;
    private static string s_version = null!;

    [ClassInitialize]
    public static void OpenMsi(TestContext context)
    {
        // Find The Repo Root And Version
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir);
        s_version = XDocument.Load(Path.Combine(dir.FullName, "Directory.Build.props")).Descendants("Version").First().Value;

        // Open The MSI Read-Only
        var msi = Path.Combine(dir.FullName, "dist", $"Layers-{s_version}.msi");
        if (!File.Exists(msi))
        {
            Assert.Inconclusive($"{msi} not built; run build.ps1");
        }

        dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer")!)!;
        s_database = installer.OpenDatabase(msi, 0);
    }

    private static List<string[]> Rows(string sql, int columns)
    {
        var rows = new List<string[]>();
        dynamic view = s_database.OpenView(sql);
        view.Execute();
        for (dynamic record = view.Fetch(); record is not null; record = view.Fetch())
        {
            rows.Add(Enumerable.Range(1, columns).Select(i => (string)record.StringData(i)).ToArray());
        }

        view.Close();
        return rows;
    }

    private static string? Property(string name) =>
        Rows($"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'", 1).FirstOrDefault()?[0];

    [TestMethod]
    public void IsPerUser()
    {
        // No ALLUSERS Plus The "No Elevation" Word Count Bit (8) Is How WiX Scope="perUser" Marks A Per-User Package
        Assert.IsNull(Property("ALLUSERS"));
        Assert.AreEqual(8, (int)s_database.SummaryInformation(0).Property(15) & 8, "summary info word count lacks the no-elevation bit");
    }

    [TestMethod]
    public void VersionMatchesProps()
    {
        Assert.AreEqual(s_version, Property("ProductVersion"));
    }

    [TestMethod]
    public void InstallsToLocalAppDataPrograms()
    {
        var directories = Rows("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3);

        Assert.IsTrue(directories.Any(d => d[0] == "INSTALLFOLDER" && d[2].EndsWith("Layers", StringComparison.Ordinal)));
        Assert.IsTrue(directories.Any(d => d[0] == "LocalAppDataFolder"));
    }

    [TestMethod]
    public void ShipsTheExe()
    {
        Assert.IsTrue(Rows("SELECT `FileName` FROM `File`", 1).Any(f => f[0].EndsWith("Layers.exe", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ShipsTheAboutPageIcon()
    {
        // The About Page Loads ms-appx:///Assets/Square150x150Logo.png, Which Must Be In The Publish Folder
        Assert.IsTrue(Rows("SELECT `FileName` FROM `File`", 1).Any(f => f[0].EndsWith("Square150x150Logo.png", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ShipsTheNoticeOnce()
    {
        Assert.AreEqual(1, Rows("SELECT `FileName` FROM `File`", 1).Count(f => f[0].EndsWith("NOTICE-fluentui.txt", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RemovesRunValueOnFullUninstallOnly()
    {
        // WixQuietExec Runs reg.exe Hidden; Its Command Line Is Set By A Type 51 Action Named After It
        var action  = Rows("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`", 4);
        var remove  = action.Single(a => a[0] == "RemoveRunValue");
        var command = action.Single(a => a[1] == "51" && a[2] == "RemoveRunValue")[3];

        Assert.AreEqual("WixQuietExec", remove[3]);
        StringAssert.Contains(command, @"reg.exe"" delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Layers /f");

        // Full Uninstall Only, And Before The Registry Is Otherwise Cleaned
        var sequence = Rows("SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`", 3).ToDictionary(r => r[0]);
        Assert.AreEqual("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", sequence["RemoveRunValue"][1]);
        Assert.IsTrue(int.Parse(sequence["RemoveRunValue"][2], System.Globalization.CultureInfo.InvariantCulture) < int.Parse(sequence["RemoveRegistryValues"][2], System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void WritesQuotedRunKey()
    {
        var run = Rows("SELECT `Root`, `Key`, `Name`, `Value` FROM `Registry`", 4)
            .Single(r => r[1] == @"Software\Microsoft\Windows\CurrentVersion\Run");

        Assert.AreEqual("1", run[0], "HKCU");
        Assert.AreEqual("Layers", run[2]);
        Assert.AreEqual("\"[INSTALLFOLDER]Layers.exe\"", run[3]);
    }

    [TestMethod]
    public void AddsStartMenuShortcut()
    {
        Assert.IsTrue(Rows("SELECT `Name` FROM `Shortcut`", 1).Any(s => s[0].EndsWith("Layers", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HasMajorUpgrade()
    {
        Assert.IsTrue(Rows("SELECT `UpgradeCode` FROM `Upgrade`", 1).Count > 0);
    }

    [TestMethod]
    public void ClosesRunningAppAndLaunchesAfterInstall()
    {
        var actions = Rows("SELECT `Action` FROM `CustomAction`", 1).Select(r => r[0]).ToList();

        Assert.IsTrue(actions.Any(a => a.Contains("CloseApplications", StringComparison.Ordinal)), "util:CloseApplication missing");
        CollectionAssert.Contains(actions, "LaunchLayers");
    }

    [TestMethod]
    public void ClosesAppBeforeFilesAreRemoved()
    {
        var sequence = Rows("SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`", 2)
            .ToDictionary(r => r[0], r => int.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture));

        var closeAction = sequence.Keys.Single(a => a.Contains("CloseApplications", StringComparison.Ordinal));

        Assert.IsTrue(sequence[closeAction] < sequence["RemoveFiles"], "app must close before RemoveFiles");
        Assert.IsTrue(sequence[closeAction] < sequence["InstallFiles"], "app must close before InstallFiles");
    }

    [TestMethod]
    public void RunAtSignInSkipsRecreatingADeletedUpgradeValue()
    {
        // The RunAtSignIn Component Only Writes On A Fresh Install, Or On Upgrade If The Value Still Exists
        var condition = Rows("SELECT `Condition` FROM `Component` WHERE `Component` = 'RunAtSignIn'", 1).Single()[0];

        Assert.AreEqual("(NOT WIX_UPGRADE_DETECTED) OR RUNATSIGNINVALUE", condition);
    }
}
