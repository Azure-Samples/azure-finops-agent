using AzureFinOps.Dashboard.AI.Runtime;

namespace Dashboard.Tests;

public sealed class HostedSearchWatchdogTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task WithoutAPendingSearchNothingIsTimed()
    {
        using var watch = new HostedSearchWatchdog(CancellationToken.None, Short);
        watch.Progress();
        await Task.Delay(Wait);

        Assert.False(watch.Token.IsCancellationRequested);
        Assert.False(watch.Stalled);
    }

    [Fact]
    public async Task APendingSearchWithoutProgressEndsTheRunAsAStall()
    {
        using var watch = new HostedSearchWatchdog(CancellationToken.None, Short);
        watch.Started("ws_1");
        watch.Progress();
        await Task.Delay(Wait);

        Assert.True(watch.Token.IsCancellationRequested);
        Assert.True(watch.Stalled);
    }

    [Fact]
    public async Task AFinishedSearchDisarmsTheWatch()
    {
        using var watch = new HostedSearchWatchdog(CancellationToken.None, Short);
        watch.Started("ws_1");
        watch.Progress();
        watch.Finished("ws_1");
        watch.Progress();
        await Task.Delay(Wait);

        Assert.False(watch.Token.IsCancellationRequested);
    }

    [Fact]
    public void TheCallersCancellationIsNeverAStall()
    {
        using var caller = new CancellationTokenSource();
        using var watch = new HostedSearchWatchdog(caller.Token, Short);
        watch.Started("ws_1");
        caller.Cancel();

        Assert.True(watch.Token.IsCancellationRequested);
        Assert.False(watch.Stalled);
    }

    [Fact]
    public void TheStallMessageAsksTheUserToRetryWithoutSdkDetail()
    {
        Assert.Equal(HostedSearchWatchdog.Message, new HostedSearchStalledException().Message);
        Assert.DoesNotContain("WebSearchCallStatus", HostedSearchWatchdog.Message);
        Assert.Contains("Ask again", HostedSearchWatchdog.Message);
    }
}
