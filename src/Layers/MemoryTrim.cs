using Windows.Win32;

namespace Layers;

/// <summary>
/// Gives memory the idle tray app no longer uses back to Windows.
/// </summary>
/// <remarks>
/// Ported from Messages' <c>MemoryTrim</c>. The app runs this once only the tray icon is left, after it has closed its
/// idle windows. Two full compacting collections (the second returns what the finalizers of closed windows freed),
/// then the native heap is compacted and the working set emptied. Windows pages the memory back in when it's needed.
/// </remarks>
internal static class MemoryTrim
{
    /// <summary>
    /// Trims now.
    /// </summary>
    /// <remarks>
    /// Blocking, and meant for when nothing is on screen.
    /// </remarks>
    public static void Run()
    {
        // Aggressive Also Compacts The Large Object Heap
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Finalizers Release The WinRT Objects Behind Closed Windows, And The Second Collection Gives That Back Too
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Merge The Native Heap's Free Blocks, Then Hand The Pages Back
        // ponytail: emptying the working set only pages memory out; the next open pays a few page faults to bring
        // back what it uses. Drop this line if that ever shows up as a slow first open.
        _ = PInvoke.HeapCompact(PInvoke.GetProcessHeap(), 0);
        _ = PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), unchecked((nuint)(-1)), unchecked((nuint)(-1)));
    }
}
