using Layers.Core.Services;
using Windows.ApplicationModel;

namespace Layers.Tests;

[TestClass]
public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\Test\AppData\Local\Programs\Layers\Layers.exe";

    [TestMethod]
    public async Task RunKey_DisabledWhenMissing()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).GetStateAsync();

        Assert.AreEqual(new StartupState(false, true, null), state);
    }

    [TestMethod]
    public async Task RunKey_EnableWritesQuotedPath()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).SetEnabledAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual($"\"{Exe}\"", temp.Get("Layers"));
    }

    [TestMethod]
    public async Task RunKey_DisableRemovesValue()
    {
        using var temp = new TempRegistryKey();
        var startup    = new RunKeyStartup(Exe, temp.Path);
        await startup.SetEnabledAsync(true);

        var state = await startup.SetEnabledAsync(false);

        Assert.IsFalse(state.IsEnabled);
        Assert.IsNull(temp.Get("Layers"));
    }

    [TestMethod]
    public async Task RunKey_DisableWhenMissingIsFine()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).SetEnabledAsync(false);

        Assert.IsFalse(state.IsEnabled);
    }

    [TestMethod]
    public async Task RunKey_SetEnabled_FailureDoesNotThrow()
    {
        // A key name over 255 characters can't be created; CreateSubKey throws ArgumentException.
        // Rooted under Software\LayersTests\ (never the real Run key) so this can never touch a real value,
        // even if the invalid-name check somehow had a side effect before throwing.
        var startup = new RunKeyStartup(Exe, @"Software\LayersTests\" + new string('a', 300));

        await startup.SetEnabledAsync(true);
    }

    [TestMethod]
    public void RunKey_DefaultPathIsCurrentUserRun()
    {
        // MSTEST0032: both sides resolve to the same compile-time literal, which is the point of this test
#pragma warning disable MSTEST0032
        Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKeyStartup.DefaultRunKeyPath);
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    public void Packaged_MapsEveryState()
    {
        Assert.AreEqual(new StartupState(true, true, null), PackagedStartupTask.Map(StartupTaskState.Enabled));
        Assert.AreEqual(new StartupState(false, true, null), PackagedStartupTask.Map(StartupTaskState.Disabled));
        Assert.AreEqual(new StartupState(false, false, "Turned off in Settings › Apps › Startup."), PackagedStartupTask.Map(StartupTaskState.DisabledByUser));
        Assert.AreEqual(new StartupState(false, false, "Turned off by your organization."), PackagedStartupTask.Map(StartupTaskState.DisabledByPolicy));
        Assert.AreEqual(new StartupState(true, false, "Turned on by your organization."), PackagedStartupTask.Map(StartupTaskState.EnabledByPolicy));
    }

    [TestMethod]
    public async Task Packaged_WinRtFailureIsNotControllable()
    {
        // The Test Process Has No Package Identity, So Every StartupTask Call Fails
        var startup  = new PackagedStartupTask();
        var expected = new StartupState(false, false, null);

        Assert.AreEqual(expected, await startup.GetStateAsync());
        Assert.AreEqual(expected, await startup.SetEnabledAsync(true));
        Assert.AreEqual(expected, await startup.SetEnabledAsync(false));
    }
}
