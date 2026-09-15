using AzureFinOps.Dashboard.AI;
using GitHub.Copilot;

namespace Dashboard.Tests;

public sealed class TranscriptRecoveryTests
{
    [Fact]
    public async Task MissingCachedHandleIsEvictedAndResumedOnce()
    {
        var evictions = 0;
        var resumes = 0;
        var expected = Array.Empty<SessionEvent>();
        var result = await CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
            () => throw new InvalidOperationException("Session not found"),
            () => { evictions++; return Task.CompletedTask; },
            () => { resumes++; return Task.FromResult<IReadOnlyList<SessionEvent>>(expected); });
        Assert.Same(expected, result);
        Assert.Equal(1, evictions);
        Assert.Equal(1, resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHistoryIsNotAnEmptyConversation(bool cached)
    {
        await Assert.ThrowsAsync<CopilotSessionFactory.HistoryUnavailableException>(() =>
            CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
                cached ? () => throw new InvalidOperationException("Session not found") : null,
                () => Task.CompletedTask,
                () => throw new InvalidOperationException("Session not found")));
    }

    [Fact]
    public async Task CancellationDoesNotTriggerRecovery()
    {
        var resumes = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
            () => throw new OperationCanceledException("Session not found"),
            () => Task.CompletedTask,
            () => { resumes++; return Task.FromResult<IReadOnlyList<SessionEvent>>([]); }));
        Assert.Equal(0, resumes);
    }
}