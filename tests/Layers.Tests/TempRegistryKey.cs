using Microsoft.Win32;

namespace Layers.Tests;

/// A throwaway HKCU key, deleted on dispose, so registry tests never touch real settings.
internal sealed class TempRegistryKey : IDisposable
{
    public string Path { get; } = $@"Software\LayersTests\{Guid.NewGuid():N}";

    public void Set(string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue(name, value, kind);
    }

    public object? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
}
