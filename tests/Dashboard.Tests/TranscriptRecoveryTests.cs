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
            () => Task.FromResult(true),
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
                () => Task.FromResult(true),
                cached ? () => throw new InvalidOperationException("Session not found") : null,
                () => Task.CompletedTask,
                () => throw new InvalidOperationException("Session not found")));
    }

    [Fact]
    public async Task CancellationDoesNotTriggerRecovery()
    {
        var resumes = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
            () => Task.FromResult(true),
            () => throw new OperationCanceledException("Session not found"),
            () => Task.CompletedTask,
            () => { resumes++; return Task.FromResult<IReadOnlyList<SessionEvent>>([]); }));
        Assert.Equal(0, resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedOwnershipDoesNotReadEvictOrResume(bool cached)
    {
        var sdkCalls = 0;
        Task<IReadOnlyList<SessionEvent>> Read()
        {
            sdkCalls++;
            return Task.FromResult<IReadOnlyList<SessionEvent>>([]);
        }

        await Assert.ThrowsAsync<CopilotSessionFactory.HistoryUnavailableException>(() =>
            CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
                () => Task.FromResult(false),
                cached ? Read : null,
                () => { sdkCalls++; return Task.CompletedTask; },
                Read));

        Assert.Equal(0, sdkCalls);
    }

    [Fact]
    public async Task WrappedSdkMissingSessionIsEvictedAndResumedOnce()
    {
        var evictions = 0;
        var resumes = 0;
        var expected = Array.Empty<SessionEvent>();
        var result = await CopilotSessionFactory.ReadTranscriptWithRecoveryAsync(
            () => Task.FromResult(true),
            () => throw new IOException("Communication error with Copilot CLI: Request session.getMessages failed with message: Session not found: test-session"),
            () => { evictions++; return Task.CompletedTask; },
            () => { resumes++; return Task.FromResult<IReadOnlyList<SessionEvent>>(expected); });

        Assert.Same(expected, result);
        Assert.Equal(1, evictions);
        Assert.Equal(1, resumes);
    }
}