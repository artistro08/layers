using System.Diagnostics;
using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;
using Layers.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers;

/// <summary>
/// Composes the app: device link, tray icon, menu, HUD, and settings.
/// </summary>
/// <remarks>
/// Only Quit (or <c>WM_CLOSE</c> from the installer) ends the process. Closing a window never does, because
/// <see cref="DispatcherShutdownMode"/> is explicit.
/// </remarks>
#pragma warning disable CA1515 // The XAML compiler generates App as public, so both halves must be public
#pragma warning disable CA1001 // App lives as long as the process; QuitAsync releases the tray icon and device link
public sealed partial class App : Application
#pragma warning restore CA1001
#pragma warning restore CA1515
{
    private DispatcherQueue _dispatcher = null!;
    private readonly TrayMenuViewModel _menuModel = new();
    private TrayIcon? _tray;
    private TrayMenuHost? _menu;
    private DeviceService? _device;
    private readonly SettingsStore _store = new();
    private HudSettings _hudSettings;
    private IStartupRegistration _startup = null!;
    private DeviceState _lastState = DeviceState.Initial;
    private HudHost? _hud;
    private bool _quitting;

    /// <summary>Creates the app.</summary>
    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException    += (_, e) =>
            Trace.TraceError("unhandled (0x{0:X8}): {1}\n{2}", e.Exception?.HResult, e.Message, e.Exception);
    }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // UI Thread Dispatcher
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            // Settings And HUD
            _hudSettings = _store.Load();
            _hud         = new HudHost();

            // Start At Sign-In, Per Packaging
            _startup = AppPackaging.IsPackaged
                ? new PackagedStartupTask()
                : new RunKeyStartup(Environment.ProcessPath!);

            // taskkill Sends WM_CLOSE Only To The Front Window, Which Is The HUD Host Once It Has Shown
            // So Closing It Quits
            _hud.Closed += (_, _) =>
            {
                _hud = null;
                _ = QuitAsync();
            };

            // Tray Icon
            _tray = new TrayIcon();
            _tray.QuitRequested += (_, _) => _ = QuitAsync();

            // Tray Menu
            _menu = new TrayMenuHost(_menuModel);
            _menu.QuitRequested     += (_, _) => _ = QuitAsync();
            _menu.SettingsRequested += (_, _) => OpenSettings();
            _tray.MenuRequested += (_, _) => _menu?.ShowAtCursor();

            // taskkill And The Installer Send WM_CLOSE To The Host Window, So Closing It Quits
            _menu.Closed += (_, _) =>
            {
                _menu = null;
                _ = QuitAsync();
            };

            // Device Link
            _device = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
            _device.StateChanged += (_, state) => _dispatcher.TryEnqueue(() => OnDeviceState(state));
            _device.Start();
        }
#pragma warning disable CA1031 // Any startup failure is shown to the user, then the app exits
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PInvoke.MessageBox(HWND.Null, ex.Message, "Layers", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);

            // Release Whatever Was Created (Device Link, Windows, Tray Icon) So No Ghost Icon Stays Behind, Then Exit
            _ = QuitAsync();
        }
    }

    private void OnDeviceState(DeviceState state)
    {
        // Decide On The HUD Before Updating
        var previous = _lastState;
        _lastState   = state;
        if (HudRules.ShouldShow(_hudSettings, previous, state))
        {
            _hud?.Show(state.Layers, _hudSettings.Hold);
        }

        // Update Tray And Menu
        _menuModel.State = state;
        _tray?.Update(state);
    }

    private void OpenSettings()
    {
        // Nothing To Open Once Quitting
        if (_quitting)
        {
            return;
        }

        // The HUD Rules Use New Settings From The Next Layer Change
        SettingsWindow.Open(() =>
        {
            var model = new SettingsViewModel(_store, _startup);
            model.HudSettingsChanged += (_, settings) => _hudSettings = settings;
            return model;
        });
    }

    /// <summary>
    /// Shuts down cleanly.
    /// </summary>
    /// <remarks>
    /// Turns Monitor mode off, closes the windows, removes the tray icon, then exits. Safe to call more than once.
    /// </remarks>
    /// <returns>A task.</returns>
    internal async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            // Monitor Off And Device Link Closed
            if (_device is not null)
            {
                await _device.DisposeAsync();
            }
        }
#pragma warning disable CA1031 // A failed device shutdown must never leave the app unquittable
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError("device shutdown failed: {0}", ex);
        }
        finally
        {
            // Always Remove The Icon And Exit
            SettingsWindow.Current?.Close();
            _menu?.Close();
            _hud?.Close();
            _tray?.Dispose();
            Exit();
        }
    }
}
