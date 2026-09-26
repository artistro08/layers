using Windows.Win32;
using Windows.Win32.Foundation;

namespace Layers.Core.Services;

/// <summary>
/// Reports whether the current process runs with MSIX package identity.
/// </summary>
/// <remarks>
/// The same binary ships unpackaged (MSI) and packaged (MSIX). A few features differ between the two:
/// start at sign-in uses the Run key unpackaged and <c>StartupTask</c> packaged.
/// </remarks>
public static class AppPackaging
{
    /// <summary>
    /// Gets a value indicating whether the process has package identity.
    /// </summary>
    /// <remarks>
    /// <c>GetCurrentPackageFullName</c> returns <c>APPMODEL_ERROR_NO_PACKAGE</c> for unpackaged processes.
    /// Any other result, including a buffer-size error, means a package is present.
    /// </remarks>
    public static unsafe bool IsPackaged
    {
        get
        {
            // Probe With A Zero-Length Buffer
            uint length = 0;
            var result  = PInvoke.GetCurrentPackageFullName(ref length, null);

            return result != WIN32_ERROR.APPMODEL_ERROR_NO_PACKAGE;
        }
    }
}
