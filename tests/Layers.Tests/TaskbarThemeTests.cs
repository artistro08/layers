using System.Security.AccessControl;
using System.Security.Principal;
using Layers.Core.Services;
using Microsoft.Win32;

namespace Layers.Tests;

[TestClass]
public sealed class TaskbarThemeTests
{
    [TestMethod]
    public void Missing_MeansDarkTaskbar()
    {
        using var temp = new TempRegistryKey();

        Assert.IsFalse(TaskbarTheme.IsLight(temp.Path));
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(0, false)]
    public void ReadsSystemUsesLightTheme(int value, bool expected)
    {
        using var temp = new TempRegistryKey();
        temp.Set("SystemUsesLightTheme", value, RegistryValueKind.DWord);

        Assert.AreEqual(expected, TaskbarTheme.IsLight(temp.Path));
    }

    [TestMethod]
    public void Unreadable_MeansDarkTaskbarInsteadOfThrowing()
    {
        using var temp = new TempRegistryKey();
        temp.Set("SystemUsesLightTheme", 1, RegistryValueKind.DWord);

        // Deny Reading The Key, So Opening It Throws SecurityException
        var deny = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.ReadKey, AccessControlType.Deny);
        using (var key = Registry.CurrentUser.OpenSubKey(temp.Path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
        {
            var security = key.GetAccessControl();
            security.AddAccessRule(deny);
            key.SetAccessControl(security);
        }

        try
        {
            Assert.IsFalse(TaskbarTheme.IsLight(temp.Path));
        }
        finally
        {
            // Lift The Deny So The Temp Key Can Be Deleted
            using var key = Registry.CurrentUser.OpenSubKey(temp.Path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
            var security  = key.GetAccessControl();
            security.RemoveAccessRule(deny);
            key.SetAccessControl(security);
        }
    }

    [TestMethod]
    public void PersonalizeKeyPath_IsTheWindowsKey()
    {
        // MSTEST0032: both sides resolve to the same compile-time literal, which is the point of this test
#pragma warning disable MSTEST0032
        Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", TaskbarTheme.PersonalizeKeyPath);
#pragma warning restore MSTEST0032
    }
}
