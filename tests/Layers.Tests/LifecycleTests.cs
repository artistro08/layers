using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Layers.Tests;

/// Launches the real Layers.exe. It touches the real tray and, if one is plugged in, the real device, exactly as the
/// app does in normal use.
[TestClass]
[DoNotParallelize]
public sealed class LifecycleTests
{
    private readonly List<Process> _started = [];

    [TestCleanup]
    public void Cleanup()
    {
        // Ask Politely First, So Monitor Mode Is Turned Off And The Icon Removed
        var running = _started.Where(p => !p.HasExited).ToList();
        if (running.Count == 0)
        {
            return;
        }

        var hwnd = PInvoke.FindWindow("LayersMessageWindow", null);
        if (!hwnd.IsNull)
        {
            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
        }

        // Kill Only What Didn't Quit In Time
        foreach (var process in running)
        {
            if (process.WaitForExit(5000))
            {
                continue;
            }

            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Exited Between The Wait And The Kill
            }
        }
    }

    private static string ExePath()
    {
        // Walk Up To The Repo Root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "repo root not found");

        // This Test Run's Configuration, From Its Own Output Path
        var segments      = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configuration = segments.Contains("Release", StringComparer.OrdinalIgnoreCase) ? "Release" : "Debug";

        // Newest Matching Build, Never A Publish Folder Or The Native AOT Intermediate (native\Layers.exe Has No Runtime Beside It)
        string[] skipped = ["publish", "native"];
        var exe = Directory.GetFiles(Path.Combine(dir.FullName, "src", "Layers", "bin"), "Layers.exe", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Contains(configuration, StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Intersect(skipped, StringComparer.OrdinalIgnoreCase).Any())
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Assert.IsNotNull(exe, $"no {configuration} Layers.exe under src\\Layers\\bin; build Layers.slnx -c {configuration} first");
        return exe;
    }

    private Process Launch()
    {
        var process = Process.Start(new ProcessStartInfo(ExePath()) { UseShellExecute = false })!;
        _started.Add(process);
        return process;
    }

    private static async Task<HWND> WaitForTrayWindowAsync()
    {
        for (var i = 0; i < 150; i++)
        {
            var hwnd = PInvoke.FindWindow("LayersMessageWindow", null);
            if (!hwnd.IsNull)
            {
                return hwnd;
            }

            await Task.Delay(100);
        }

        Assert.Fail("tray window never appeared");
        return HWND.Null;
    }

    [TestInitialize]
    public void RequireNoRunningLayers()
    {
        if (Process.GetProcessesByName("Layers").Length > 0)
        {
            Assert.Inconclusive("Layers is already running; quit it to run lifecycle tests");
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Starts_AndCreatesTrayWindow()
    {
        var first = Launch();

        await WaitForTrayWindowAsync();

        Assert.IsFalse(first.HasExited);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SecondInstance_ExitsSilently()
    {
        var first = Launch();
        await WaitForTrayWindowAsync();

        var second = Launch();
        var exited = second.WaitForExit(15_000);

        Assert.IsTrue(exited, "second instance kept running");
        Assert.AreEqual(0, second.ExitCode);
        Assert.IsFalse(first.HasExited);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task WmClose_QuitsCleanly()
    {
        var first = Launch();
        var hwnd  = await WaitForTrayWindowAsync();

        PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);

        Assert.IsTrue(first.WaitForExit(15_000), "app did not quit on WM_CLOSE");
        Assert.AreEqual(0, first.ExitCode);
        Assert.IsTrue(PInvoke.FindWindow("LayersMessageWindow", null).IsNull, "tray window left behind");
    }
}
