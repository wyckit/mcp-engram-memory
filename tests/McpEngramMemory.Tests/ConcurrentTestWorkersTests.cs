namespace McpEngramMemory.Tests;

public sealed class ConcurrentTestWorkersTests
{
    [Fact]
    public void WorkerFailure_CancelsAndJoinsPeersBeforeReturning()
    {
        var workers = new ConcurrentTestWorkers();
        using var peerStarted = new ManualResetEventSlim();
        bool peerExited = false;
        var error = Record.Exception(() => workers.Run(TimeSpan.FromSeconds(10),
            token => { peerStarted.Wait(token); throw new InvalidOperationException("injected failure"); },
            token =>
            {
                try { peerStarted.Set(); token.WaitHandle.WaitOne(); }
                finally { peerExited = true; }
            }));

        Assert.NotNull(error);
        Assert.Contains("injected failure", error.ToString());
        Assert.True(peerExited);
        Assert.True(workers.IsQuiescent);
    }

    [Fact]
    public void Deadline_CancelsAndJoinsWorkersBeforeReturning()
    {
        var workers = new ConcurrentTestWorkers();
        var error = Record.Exception(() => workers.Run(TimeSpan.FromMilliseconds(100),
            token => token.WaitHandle.WaitOne()));

        Assert.NotNull(error);
        Assert.True(workers.IsQuiescent);
    }
}
