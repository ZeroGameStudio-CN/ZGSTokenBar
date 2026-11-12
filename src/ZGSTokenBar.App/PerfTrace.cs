using System.Diagnostics;

namespace ZGSTokenBar.App;

/// <summary>
/// Opt-in UI latency trace (ZGSTOKENBAR_PERF_TRACE=&lt;file&gt;). Off by default and then costs one
/// null check; when on, every <see cref="Measure"/> scope appends "name ms" to the file.
/// </summary>
internal static class PerfTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("ZGSTOKENBAR_PERF_TRACE") is { Length: > 0 } path
        && System.IO.Path.IsPathFullyQualified(path) ? path : null;
    private static readonly object Gate = new();

    internal static bool Enabled => Path is not null;

    internal static Scope Measure(string name) => new(Path is null ? null : name);

    internal static void Note(string text)
    {
        if (Path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal readonly struct Scope(string? name) : IDisposable
    {
        private readonly long _start = name is null ? 0 : Stopwatch.GetTimestamp();

        public void Dispose()
        {
            if (name is null) return;
            Note($"{name} {Stopwatch.GetElapsedTime(_start).TotalMilliseconds:0.00}ms");
        }
    }
}
