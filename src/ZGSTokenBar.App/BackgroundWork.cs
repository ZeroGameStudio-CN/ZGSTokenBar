namespace ZGSTokenBar.App;

/// <summary>
/// Runs best-effort local scans (session logs, token indexes) at below-normal thread priority so
/// a cold start or a large catch-up never competes with Explorer or the UI thread for a core.
/// </summary>
internal static class BackgroundWork
{
    public static T BelowNormal<T>(Func<T> work)
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        try
        {
            if (previous > ThreadPriority.BelowNormal) thread.Priority = ThreadPriority.BelowNormal;
        }
        catch (ThreadStateException)
        {
            return work();
        }

        try
        {
            return work();
        }
        finally
        {
            try { thread.Priority = previous; }
            catch (ThreadStateException) { }
        }
    }
}
