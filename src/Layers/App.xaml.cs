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
/// <see cref="DispatcherShutdownMode"/> is explicit. At rest the app is just the tray icon and the device link: the
/// tray menu, shortcut sheet, HUD, and Settings are each made when first needed and closed again when done (the menu
/// at once, the sheet and HUD after 30 s hidden, so a quick reopen stays instant), then <see cref="MemoryTrim"/>
/// hands the freed memory back to Windows.
/// </remarks>
#pragma warning disable CA1515 // The XAML compiler generates App as public, so both halves must be public
#pragma warning disable CA1001 // App lives as long as the process; QuitAsync releases the tray icon and device link
public sealed partial class App : Application
#pragma warning restore CA1001
#pragma warning restore CA1515
{
    // How Long A Hidden Flyout Or HUD Stays Around For A Quick Reopen Before It's Closed And Memory Is Trimmed
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private DispatcherQueue _dispatcher = null!;
    private DispatcherQueueTimer _trimTimer = null!;
    private readonly TrayMenuViewModel _menuModel = new();
    private readonly ShortcutSheetViewModel _sheetModel = new();
    private TrayIcon? _tray;
    private TrayMenuHost? _menu;
    private ShortcutFlyoutWindow? _flyout;
    private GlobalHotkey? _hotkey;
    private Hotkey? _shortcut;
    private DispatcherQueueTimer? _refreshTimer;
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
            // UI Thread Dispatcher And Memory Trim
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _trimTimer  = _dispatcher.CreateTimer();
            _trimTimer.IsRepeating = false;
            _trimTimer.Tick       += (_, _) =>
            {
                if (ReleaseIdleWindows())
                {
                    MemoryTrim.Run();
                }
            };

            // Settings
            _hudSettings = _store.Load();

            // Start At Sign-In, Per Packaging
            _startup = AppPackaging.IsPackaged
                ? new PackagedStartupTask()
                : new RunKeyStartup(Environment.ProcessPath!);

            // Tray Icon: Its Hidden Window Also Gets taskkill's And The Installer's WM_CLOSE, Which Quits
            _tray = new TrayIcon(AppPackaging.IsPackaged);
            _tray.QuitRequested += (_, _) => _ = QuitAsync();
            _tray.MenuRequested += (_, _) => ShowMenu();

            // Shortcut Sheet: Tray Left-Click, The Menu Item, And The Hotkey Toggle It
            _sheetModel.Meanings   = _store.LoadMeanings();
            _tray.FlyoutRequested += (_, _) => Flyout()?.Toggle(_tray?.GetIconRect());
            _tray.HotkeyPressed   += (_, id) =>
            {
                if (id == GlobalHotkey.Id)
                {
                    Flyout()?.Toggle(Windhawk.HasTrayOnEveryTaskbar() ? null : _tray?.GetIconRect());
                }
            };

            // Global Hotkey On The Tray Window
            _hotkey   = new GlobalHotkey(_tray.Handle);
            _shortcut = _store.LoadShortcut();
            _hotkey.Apply(_shortcut);

            // Device Link
            _device = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
            _device.StateChanged  += (_, state) => _dispatcher.TryEnqueue(() => OnDeviceState(state));
            _device.ConfigChanged += (_, config) => _dispatcher.TryEnqueue(() => OnDeviceConfig(config));
            _device.Start();

            // First Trim Once Startup And The Device Link Have Settled
            ScheduleTrim();
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
            Hud()?.Show(state.Layers, _hudSettings.Hold, _hudSettings.HudMonitor);
        }

        // Update Tray, Menu, And Sheet
        _menuModel.State  = state;
        _sheetModel.State = state;
        _tray?.Update(state);
    }

    // =========================================================================
    // WINDOWS, MADE WHEN NEEDED
    // =========================================================================

    // Opens The Tray Menu In A Fresh Host, Closed Again As Soon As The Menu Closes
    private void ShowMenu()
    {
        if (_quitting)
        {
            return;
        }

        if (_menu is null)
        {
            var menu = new TrayMenuHost(_menuModel);
            menu.QuitRequested      += (_, _) => _ = QuitAsync();
            menu.SettingsRequested  += (_, _) => OpenSettings();
            menu.ShortcutsRequested += (_, _) => Flyout()?.Open(_tray?.GetIconRect());

            // Closed After The Click That Closed The Menu Has Finished, Unless A New Right-Click Reopened It
            menu.MenuClosed += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(_menu, menu) && !menu.IsMenuOpen)
                {
                    _menu = null;
                    menu.Close();
                    ScheduleTrim();
                }
            });

            QuitOnUnaskedClose(menu, () =>
            {
                if (!ReferenceEquals(_menu, menu))
                {
                    return false;
                }

                _menu = null;
                return true;
            });
            _menu = menu;
        }

        _menu.ShowAtCursor();
    }

    // The Shortcut Sheet Flyout, Made On First Use And Released Once It Has Been Hidden A While
    private ShortcutFlyoutWindow? Flyout()
    {
        if (_quitting)
        {
            return null;
        }

        if (_flyout is null)
        {
            var flyout = new ShortcutFlyoutWindow(_sheetModel);
            flyout.CloseRequested    += (_, _) => flyout.HideFlyout();
            flyout.SettingsRequested += (_, _) =>
            {
                flyout.HideFlyout();
                OpenSettings();
            };
            flyout.EditRequested += (_, _) =>
            {
                flyout.HideFlyout();
                _ = Windows.System.Launcher.LaunchUriAsync(_store.LoadEditorUrl());
            };
            flyout.Opened += (_, _) => RequestSheetRefresh();
            flyout.Hidden += (_, _) => ScheduleTrim();
            _flyout = flyout;
        }

        return _flyout;
    }

    // The Layer-Change HUD, Made On The First Change And Released Once It Has Been Hidden A While
    private HudHost? Hud()
    {
        if (_quitting)
        {
            return null;
        }

        if (_hud is null)
        {
            var hud = new HudHost();
            hud.Hidden += (_, _) => ScheduleTrim();
            QuitOnUnaskedClose(hud, () =>
            {
                if (!ReferenceEquals(_hud, hud))
                {
                    return false;
                }

                _hud = null;
                return true;
            });
            _hud = hud;
        }

        return _hud;
    }

    // taskkill And The Installer Send WM_CLOSE To The Front Window, Which Can Be The Menu Or HUD Host, So A Close We
    // Didn't Ask For Quits. The App Forgets A Window Before Closing It, So forget() Then Returns False. The Handler
    // Removes Itself, Since A Window Left Handling Its Own Closed Is Never Freed.
    private void QuitOnUnaskedClose(Window window, Func<bool> forget)
    {
        void Window_Closed(object sender, WindowEventArgs args)
        {
            window.Closed -= Window_Closed;
            if (forget())
            {
                _ = QuitAsync();
            }
        }

        window.Closed += Window_Closed;
    }

    // Restarts The Wait, So A Burst Of Hides And Closes Ends In One Release And Trim
    private void ScheduleTrim()
    {
        _trimTimer.Stop();
        _trimTimer.Interval = IdleDelay;
        _trimTimer.Start();
    }

    // Runs Before Each Trim: Closes The Flyout And HUD If They're Hidden, And Reports Whether Only The Tray Is Left
    // (A Window Still On Screen Hides Later, And Its Hide Schedules The Next Try)
    private bool ReleaseIdleWindows()
    {
        if (_quitting)
        {
            return false;
        }

        if (_flyout is { IsOpen: false } flyout && !flyout.AppWindow.IsVisible)
        {
            _flyout = null;
            _refreshTimer?.Stop();
            flyout.Shutdown();
        }

        if (_hud is { IsShown: false } hud)
        {
            _hud = null;
            hud.Close();
        }

        return _flyout is null && _hud is null && _menu is null && SettingsWindow.Current is null;
    }

    private void OnDeviceConfig(DeviceConfig config)
    {
        _refreshTimer?.Stop();
        _sheetModel.Config        = config;
        _sheetModel.RefreshFailed = false;
    }

    // A Read That Produces Nothing Within 5 Seconds Is Reported On The Sheet, Which Keeps Its Last List
    private void RequestSheetRefresh()
    {
        if (_device is null)
        {
            return;
        }

        _refreshTimer ??= _dispatcher.CreateTimer();
        _refreshTimer.Stop();
        _refreshTimer.Interval  = TimeSpan.FromSeconds(5);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick     -= RefreshTimer_Tick;
        _refreshTimer.Tick     += RefreshTimer_Tick;
        _sheetModel.RefreshFailed = false;
        _device.RequestConfigRefresh();
        _refreshTimer.Start();
    }

    // With A List The Header Says "Couldn't refresh"; Without One The Body Says "Couldn't read shortcuts"
    private void RefreshTimer_Tick(DispatcherQueueTimer sender, object args) =>
        _sheetModel.RefreshFailed = true;

    private void OpenSettings()
    {
        // Nothing To Open Once Quitting
        if (_quitting)
        {
            return;
        }

        // The HUD Rules Use New Settings From The Next Layer Change; The Hotkey Re-Registers On Each Change
        var isNew  = SettingsWindow.Current is null;
        var window = SettingsWindow.Open(() =>
        {
            var model = new SettingsViewModel(_store, _startup)
            {
                // The Expressions The Device Maps, From The Last Read, For The Meaning Cards
                ExpressionUses = _device?.Config is { } config ? ExpressionMeaning.Uses(config) : [],
            };
            model.MeaningsChanged += (_, meanings) => _sheetModel.Meanings = meanings;
            model.HudSettingsChanged += (_, settings) => _hudSettings = settings;
            model.IsHotkeyFree        = hotkey => _hotkey?.IsFree(hotkey) ?? true;
            model.ShortcutTaken       = _hotkey?.IsTaken ?? false;
            model.ShortcutChanged    += (_, hotkey) =>
            {
                _shortcut = hotkey;
                ApplyHotkey(model);
            };

            // The Picker's Dialog Listens For Keys: Let Go Of Ours Meanwhile, So Pressing It Reaches The Dialog
            model.ShortcutDialogOpenChanged += (_, open) =>
            {
                if (open)
                {
                    _hotkey?.Apply(null);
                }
                else
                {
                    ApplyHotkey(model);
                }
            };
            return model;
        });

        // Settings Destroys Itself On Close, Then The Memory It Used Goes Back
        if (isNew)
        {
            window.Closed += (_, _) => ScheduleTrim();
        }
    }

    private void ApplyHotkey(SettingsViewModel model)
    {
        _hotkey?.Apply(_shortcut);
        model.ShortcutTaken = _hotkey?.IsTaken ?? false;
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
            _trimTimer?.Stop();
            _refreshTimer?.Stop();
            SettingsWindow.Current?.Close();
            _flyout?.Shutdown();
            _menu?.Close();
            _hud?.Close();
            _hotkey?.Dispose();
            _tray?.Dispose();
            Exit();
        }
    }
}
