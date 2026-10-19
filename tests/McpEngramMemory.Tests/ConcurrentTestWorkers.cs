using System.Collections.Concurrent;
using System.Diagnostics;

namespace McpEngramMemory.Tests;

/// <summary>Bounded dedicated workers, with cancellation and a drain on every exit path.</summary>
internal sealed class ConcurrentTestWorkers
{
    private Thread[] _threads = Array.Empty<Thread>();
    public bool IsQuiescent => _threads.All(t => !t.IsAlive);

    public void Run(TimeSpan timeout, params Action<CancellationToken>[] actions)
    {
        var stop = new CancellationTokenSource();
        var ready = new Barrier(actions.Length + 1);
        var failures = new ConcurrentQueue<Exception>();
        _threads = actions.Select((action, i) => new Thread(() =>
        {
            try
            {
                ready.SignalAndWait(stop.Token);
                action(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { failures.Enqueue(ex); stop.Cancel(); }
        }) { IsBackground = true, Name = $"regression-worker-{i}" }).ToArray();

        var elapsed = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            foreach (var thread in _threads) thread.Start();
            if (ready.SignalAndWait(timeout))
                completed = JoinWithin(timeout - elapsed.Elapsed);
        }
        finally
        {
            stop.Cancel();
            // Includes startup failures, assertion failures and timeout. Never tear down a
            // barrier or token while a worker can still use it. Fixtures also check IsQuiescent
            // before disposing the index if a real, uninterruptible lock deadlock was detected.
            JoinWithin(TimeSpan.FromSeconds(10));
            if (IsQuiescent) { ready.Dispose(); stop.Dispose(); }
        }

        Assert.True(IsQuiescent, "Workers did not stop after cancellation; fixture resources were retained to avoid disposal under live work.");
        Assert.Empty(failures);
        Assert.True(completed, $"Workers exceeded {timeout.TotalSeconds}s; all workers were cancelled and joined. A timeout alone does not establish a deadlock.");
    }

    private bool JoinWithin(TimeSpan budget)
    {
        var elapsed = Stopwatch.StartNew();
        foreach (var thread in _threads)
        {
            if (!thread.IsAlive) continue;
            var remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero || !thread.Join(remaining)) return false;
        }
        return true;
    }
}
