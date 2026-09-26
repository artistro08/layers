using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Layers.Core.Services;

/// <summary>
/// Whether the app starts at sign-in, and whether the user can change that here.
/// </summary>
/// <remarks>
/// <see cref="Note"/> explains why the switch is locked, when it is.
/// </remarks>
/// <param name="IsEnabled">Starts at sign-in.</param>
/// <param name="IsControllable">The Settings switch can change it.</param>
/// <param name="Note">Shown under a locked switch. Verbatim user-facing text.</param>
public sealed record StartupState(bool IsEnabled, bool IsControllable, string? Note);

/// <summary>
/// Controls start at sign-in.
/// </summary>
/// <remarks>
/// <see cref="RunKeyStartup"/> for the MSI build and <see cref="PackagedStartupTask"/> for the MSIX build.
/// The app picks one at launch with <see cref="AppPackaging.IsPackaged"/>.
/// </remarks>
public interface IStartupRegistration
{
    /// <summary>Reads the current state.</summary>
    /// <remarks>Called when the Settings window opens.</remarks>
    /// <returns>The state.</returns>
    Task<StartupState> GetStateAsync();

    /// <summary>Turns start at sign-in on or off.</summary>
    /// <remarks>Returns the resulting state, which may differ from the request (for example when Windows refuses).</remarks>
    /// <param name="enabled">The requested state.</param>
    /// <returns>The resulting state.</returns>
    Task<StartupState> SetEnabledAsync(bool enabled);
}

/// <summary>
/// Start at sign-in through the HKCU Run key.
/// </summary>
/// <remarks>
/// The value is the quoted full exe path, so a path with spaces can't be misread. The MSI writes the same value at
/// install and removes it at uninstall.
/// </remarks>
/// <param name="exePath">Full path of Layers.exe.</param>
/// <param name="runKeyPath">The HKCU-relative Run key. Tests pass a throwaway key.</param>
public sealed class RunKeyStartup(string exePath, string runKeyPath = RunKeyStartup.DefaultRunKeyPath) : IStartupRegistration
{
    /// <summary>The per-user Run key.</summary>
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value name.</summary>
    public const string ValueName = "Layers";

    /// <inheritdoc/>
    public Task<StartupState> GetStateAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return Task.FromResult(new StartupState(key?.GetValue(ValueName) is string, true, null));
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("run key read failed: {0}", ex.Message);
            return Task.FromResult(new StartupState(false, true, null));
        }
    }

    /// <inheritdoc/>
    public async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("run key update failed: {0}", ex.Message);
        }

        return await GetStateAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Start at sign-in through the MSIX <c>StartupTask</c> extension.
/// </summary>
/// <remarks>
/// The task is declared in <c>Package.appxmanifest</c> with ID <c>LayersStartup</c>, enabled by default.
/// Windows can lock it: if the user turned it off in Settings, only Settings can turn it back on.
/// A failed WinRT call is traced and reads as locked with no note, so it never crashes the app.
/// </remarks>
public sealed class PackagedStartupTask : IStartupRegistration
{
    /// <summary>The manifest task ID.</summary>
    public const string TaskId = "LayersStartup";

    /// <inheritdoc/>
    public Task<StartupState> GetStateAsync() => GuardAsync(async () =>
    {
        var task = await StartupTask.GetAsync(TaskId);
        return Map(task.State);
    });

    /// <inheritdoc/>
    public Task<StartupState> SetEnabledAsync(bool enabled) => GuardAsync(async () =>
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (enabled)
        {
            return Map(await task.RequestEnableAsync());
        }

        task.Disable();
        return Map(task.State);
    });

    // A Failed WinRT Call Locks The Switch Instead Of Reaching The Settings Window's async void Toggled Handler
    private static async Task<StartupState> GuardAsync(Func<Task<StartupState>> call)
    {
        try
        {
            return await call().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Any WinRT failure (no package identity, missing task, RPC error) maps to a locked switch
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning("startup task call failed: {0}", ex.Message);
            return new StartupState(false, false, null);
        }
    }

    /// <summary>Maps a Windows task state to a <see cref="StartupState"/>.</summary>
    /// <remarks>Notes are verbatim user-facing text.</remarks>
    /// <param name="state">The Windows state.</param>
    /// <returns>The mapped state.</returns>
    internal static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled          => new StartupState(true, true, null),
        StartupTaskState.Disabled         => new StartupState(false, true, null),
        StartupTaskState.DisabledByUser   => new StartupState(false, false, "Turned off in Settings › Apps › Startup."),
        StartupTaskState.DisabledByPolicy => new StartupState(false, false, "Turned off by your organization."),
        StartupTaskState.EnabledByPolicy  => new StartupState(true, false, "Turned on by your organization."),
        _                                 => new StartupState(false, false, null),
    };
}
