using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;

namespace Layers.Tests;

[TestClass]
public sealed class SettingsViewModelTests : IDisposable
{
    private sealed class FakeStartup(StartupState state) : IStartupRegistration
    {
        public StartupState State { get; private set; } = state;
        public List<bool> Requests { get; } = [];

        public Task<StartupState> GetStateAsync() => Task.FromResult(State);

        public Task<StartupState> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            State = State with { IsEnabled = enabled };
            return Task.FromResult(State);
        }
    }

    private TempRegistryKey _key = null!;
    private SettingsStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _key   = new TempRegistryKey();
        _store = new SettingsStore(_key.Path);
    }

    [TestCleanup]
    public void Cleanup() => _key.Dispose();

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();

    private SettingsViewModel Create(StartupState? startup = null) =>
        new(_store, new FakeStartup(startup ?? new StartupState(true, true, null)));

    [TestMethod]
    public void Loads_StoredSettings()
    {
        _store.Save(new HudSettings(false, 0b100, 1700));

        var model = Create();

        Assert.IsFalse(model.HudEnabled);
        Assert.IsFalse(model.Layers[2].IsShown);
        Assert.IsTrue(model.Layers[1].IsShown);
    }

    [TestMethod]
    public void Layers_AreZeroThroughSeven()
    {
        var model = Create();

        Assert.AreEqual(8, model.Layers.Count);
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => $"Layer {i}").ToArray(), model.Layers.Select(l => l.Label).ToArray());
    }

    [TestMethod]
    public void HudToggle_SavesAndNotifies()
    {
        var model = Create();
        HudSettings? raised = null;
        model.HudSettingsChanged += (_, s) => raised = s;

        model.HudEnabled = false;

        Assert.AreEqual(new HudSettings(false, 0, 1700), _store.Load());
        Assert.AreEqual(new HudSettings(false, 0, 1700), raised);
    }

    [TestMethod]
    public void UncheckingLayer_SetsSuppressedBit()
    {
        var model = Create();

        model.Layers[3].IsShown = false;

        Assert.AreEqual((byte)0b1000, _store.Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void CheckingLayer_ClearsSuppressedBit()
    {
        _store.Save(new HudSettings(true, 0b1001, 1700));
        var model = Create();

        model.Layers[3].IsShown = true;

        Assert.AreEqual((byte)0b0001, _store.Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void LayerOptions_DisabledWhenHudOff()
    {
        var model   = Create();
        var changed = new List<string?>();
        model.Layers[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.HudEnabled = false;

        Assert.IsTrue(model.Layers.All(l => !l.IsEnabled));
        CollectionAssert.Contains(changed, nameof(LayerOptionViewModel.IsEnabled));
    }

    [TestMethod]
    public void HudHold_LoadsStoredSeconds()
    {
        _store.Save(new HudSettings(true, 0, 2300));

        Assert.AreEqual(2.3, Create().HudHoldSeconds, 1e-9);
    }

    [TestMethod]
    public void HudHold_SavesAndNotifies()
    {
        var model   = Create();
        var changed = new List<string?>();
        HudSettings? raised = null;
        model.HudSettingsChanged += (_, s) => raised = s;
        model.PropertyChanged    += (_, e) => changed.Add(e.PropertyName);

        model.HudHoldSeconds = 2.5;

        Assert.AreEqual(2500, _store.Load().HudHoldMs);
        Assert.AreEqual(new HudSettings(true, 0, 2500), raised);
        CollectionAssert.Contains(changed, nameof(SettingsViewModel.HudHoldSeconds));
    }

    [TestMethod]
    public void HudHold_EchoDoesNothing()
    {
        var model  = Create();
        var raised = 0;
        model.HudSettingsChanged += (_, _) => raised++;

        // A Slider Echo Of The Same Value, With Floating-Point Noise
        model.HudHoldSeconds = 1.7000000000000002;

        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    [DataRow(0.1, 500)]
    [DataRow(9.0, 5000)]
    [DataRow(1.23, 1200)]
    public void HudHold_ClampsAndSnapsToTenths(double seconds, int expectedMs)
    {
        var model = Create();

        model.HudHoldSeconds = seconds;

        Assert.AreEqual(expectedMs, _store.Load().HudHoldMs);
    }

    [TestMethod]
    public async Task Startup_LoadsState()
    {
        var model = Create(new StartupState(false, false, "Turned off in Settings › Apps › Startup."));

        await model.LoadStartupAsync();

        Assert.IsFalse(model.StartAtSignIn);
        Assert.IsFalse(model.StartupControllable);
        Assert.IsTrue(model.HasStartupNote);
        Assert.AreEqual("Turned off in Settings › Apps › Startup.", model.StartupNote);
    }

    [TestMethod]
    public async Task Startup_ToggleCallsRegistration()
    {
        var startup = new FakeStartup(new StartupState(true, true, null));
        var model   = new SettingsViewModel(_store, startup);
        await model.LoadStartupAsync();

        await model.SetStartAtSignInAsync(false);

        CollectionAssert.AreEqual(new[] { false }, startup.Requests);
        Assert.IsFalse(model.StartAtSignIn);
    }

    [TestMethod]
    public async Task Startup_SameValueDoesNothing()
    {
        var startup = new FakeStartup(new StartupState(true, true, null));
        var model   = new SettingsViewModel(_store, startup);
        await model.LoadStartupAsync();

        await model.SetStartAtSignInAsync(true);

        Assert.AreEqual(0, startup.Requests.Count);
    }

    [TestMethod]
    public void Version_IsAssemblyVersion()
    {
        Assert.AreEqual("2.0.0", Create().Version);
    }
}
