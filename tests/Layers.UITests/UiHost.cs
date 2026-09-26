using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Layers.UITests;

/// Starts one WinUI Application on a dedicated STA thread for the whole test run.
[TestClass]
public static class UiHost
{
    private static DispatcherQueue? s_queue;
    private static Thread? s_thread;

    [AssemblyInitialize]
    public static void Start(TestContext context)
    {
        // Start The App On Its Own UI Thread
        using var ready = new ManualResetEventSlim();
        s_thread        = new Thread(() =>
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callbackParams =>
            {
                s_queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(s_queue));
                _ = new TestApp();
                ready.Set();
            });
        });
        s_thread.SetApartmentState(ApartmentState.STA);
        s_thread.IsBackground = true;
        s_thread.Start();

        // Wait For It
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WinUI host did not start");
        }
    }

    [AssemblyCleanup]
    public static void Stop()
    {
        // Nothing To Stop If The Host Never Started
        if (s_queue is null || s_thread is null)
        {
            return;
        }

        // Exit, Then Wait For Application.Start To Return, So XAML Teardown (Which Calls Back Into Managed Closed And
        // Unloaded Handlers) Finishes Before The Runtime Shuts Down; Returning Early Raced Process Exit (0xC0000005 In coreclr)
        if (s_queue.TryEnqueue(() => Application.Current.Exit()) && !s_thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WinUI host did not exit");
        }
    }

    /// Runs a test body on the UI thread and surfaces its exceptions to MSTest.
    public static Task RunAsync(Func<Task> body)
    {
        var done   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = s_queue!.TryEnqueue(async () =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        if (!queued)
        {
            throw new InvalidOperationException("UI thread is gone");
        }

        return done.Task;
    }

    /// Lets layout and bindings settle.
    public static Task Settle() => Task.Delay(300);
}
