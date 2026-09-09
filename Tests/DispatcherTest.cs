using System.Runtime.ExceptionServices;
using System.Windows.Threading;

internal static class DispatcherTest
{
    // Async view-model work must resume on the WPF thread, just as it does in the app.
    public static void Run(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new DispatcherSynchronizationContext();
        var frame = new DispatcherFrame();
        Exception? error = null;
        SynchronizationContext.SetSynchronizationContext(context);
        context.Post(async _ =>
        {
            try { await test(); }
            catch (Exception ex) { error = ex; }
            finally { frame.Continue = false; }
        }, null);
        try { Dispatcher.PushFrame(frame); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
